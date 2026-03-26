using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
using Lumi.Models;
using Microsoft.Extensions.AI;

namespace Lumi.Services;

public enum CopilotSignInResult
{
    Success,
    CliNotFound,
    Failed,
}

public class CopilotService : IAsyncDisposable
{
    private CopilotClient? _client;
    /// <summary>Exposes the underlying CopilotClient for advanced usage (e.g. test harness).</summary>
    public CopilotClient? Client => _client;
    private List<ModelInfo>? _models;
    private string? _fastestModelId;
    private long _connectionGeneration;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    /// <summary>Fires after the CopilotClient has been replaced (reconnection).
    /// Consumers should discard any cached CopilotSession objects.</summary>
    public event Action? Reconnected;

    public bool IsConnected => _client?.State == ConnectionState.Connected;

    /// <summary>Monotonically increasing generation counter. Changes every time a
    /// new CopilotClient is created, allowing consumers to detect stale sessions.</summary>
    public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);

    public async Task ConnectAsync(CancellationToken ct = default)
        => await ConnectCoreAsync(forceReconnect: false, ct);

    public async Task ForceReconnectAsync(CancellationToken ct = default)
        => await ConnectCoreAsync(forceReconnect: true, ct);

    private async Task ConnectCoreAsync(bool forceReconnect, CancellationToken ct)
    {
        CopilotClient? oldClient = null;

        await _connectGate.WaitAsync(ct);
        try
        {
            if (!forceReconnect && _client?.State == ConnectionState.Connected)
                return;

            oldClient = _client;
            var cliPath = FindCliPath();
            var clientOptions = new CopilotClientOptions
            {
                CliPath = cliPath ?? "copilot",
                LogLevel = "error",
                AutoRestart = true,
            };

            ConfigureAuthentication(clientOptions);

            var newClient = new CopilotClient(clientOptions);
            await newClient.StartAsync(ct);

            _client = newClient;
            _models = null;
            _fastestModelId = null;
            Interlocked.Increment(ref _connectionGeneration);
        }
        finally
        {
            _connectGate.Release();
        }

        // Dispose the old client (stops the old CLI process) after the new one is ready.
        if (oldClient is not null && !ReferenceEquals(oldClient, _client))
        {
            Reconnected?.Invoke();
            try { await oldClient.DisposeAsync(); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Pings the CLI process with a timeout. Returns false if
    /// the client is missing, disconnected, or unresponsive.</summary>
    public async Task<bool> IsHealthyAsync(TimeSpan? timeout = null)
    {
        if (_client is null || _client.State != ConnectionState.Connected)
            return false;
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(8));
            await _client.PingAsync(cancellationToken: cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        _models ??= await _client.ListModelsAsync(ct);
        return _models;
    }

    /// <summary>Returns the cheapest/fastest model ID from the cached model list.
    /// Uses billing multiplier as a proxy for speed — lower cost ≈ faster/lighter.</summary>
    public async Task<string?> GetFastestModelIdAsync(CancellationToken ct = default)
    {
        if (_fastestModelId is not null) return _fastestModelId;
        try
        {
            var models = await GetModelsAsync(ct);
            _fastestModelId = models
                .Where(m => m.Billing is not null)
                .OrderBy(m => m.Billing!.Multiplier)
                .FirstOrDefault()?.Id;
        }
        catch { /* best effort — fall back to default model */ }
        return _fastestModelId;
    }

    /// <summary>Creates a new Copilot session with the given configuration.</summary>
    public async Task<CopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.CreateSessionAsync(config, ct);
    }

    /// <summary>Resumes an existing Copilot session by ID.</summary>
    public async Task<CopilotSession> ResumeSessionAsync(
        string sessionId, ResumeSessionConfig config, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.ResumeSessionAsync(sessionId, config, ct);
    }

    /// <summary>Retrieves the event log for a session (for replay/restore).</summary>
    public async Task<IReadOnlyList<SessionEvent>> GetSessionEventsAsync(
        CopilotSession session, CancellationToken ct = default)
    {
        return await session.GetMessagesAsync(ct);
    }

    /// <summary>Lists all known sessions, optionally filtered.</summary>
    public async Task<List<SessionMetadata>> ListSessionsAsync(
        SessionListFilter? filter = null, CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.ListSessionsAsync(filter, ct);
    }

    public async Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.GetAuthStatusAsync(ct);
    }

    public string? GetStoredLogin()
    {
        try
        {
            return GetStoredCopilotIdentity().Login;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lists built-in agents available in a session via the RPC Agent API.</summary>
    public async Task<List<GitHub.Copilot.SDK.Rpc.Agent>> ListSessionAgentsAsync(
        CopilotSession session, CancellationToken ct = default)
    {
        var result = await session.Rpc.Agent.ListAsync(ct);
        return result.Agents;
    }

    /// <summary>Selects a built-in agent by name for all future turns in the session.</summary>
    public async Task SelectSessionAgentAsync(
        CopilotSession session, string agentName, CancellationToken ct = default)
    {
        await session.Rpc.Agent.SelectAsync(agentName, ct);
    }

    /// <summary>Deselects the current built-in agent, returning the session to default routing.</summary>
    public async Task DeselectSessionAgentAsync(
        CopilotSession session, CancellationToken ct = default)
    {
        await session.Rpc.Agent.DeselectAsync(ct);
    }

    // ── Plan API ──

    /// <summary>Reads the current plan content from the session.</summary>
    public async Task<(bool Exists, string? Content)> ReadSessionPlanAsync(
        CopilotSession session, CancellationToken ct = default)
    {
        var result = await session.Rpc.Plan.ReadAsync(ct);
        return (result.Exists == true, result.Content);
    }

    /// <summary>Updates the plan content for the session.</summary>
    public async Task UpdateSessionPlanAsync(
        CopilotSession session, string content, CancellationToken ct = default)
    {
        await session.Rpc.Plan.UpdateAsync(content, ct);
    }

    /// <summary>Deletes the current plan from the session.</summary>
    public async Task DeleteSessionPlanAsync(
        CopilotSession session, CancellationToken ct = default)
    {
        await session.Rpc.Plan.DeleteAsync(ct);
    }

    // ── Model API (mid-session switching) ──

    /// <summary>Switches the model mid-session without recreating it.</summary>
    public async Task<string?> SwitchSessionModelAsync(
        CopilotSession session, string modelId, CancellationToken ct = default)
    {
        var result = await session.Rpc.Model.SwitchToAsync(modelId, ct);
        return result.ModelId;
    }

    // ── Account API ──

    /// <summary>Gets the current account quota information.</summary>
    public async Task<GitHub.Copilot.SDK.Rpc.AccountGetQuotaResult?> GetAccountQuotaAsync(CancellationToken ct = default)
    {
        if (_client is null) return null;
        return await _client.Rpc.Account.GetQuotaAsync(ct);
    }

    // ── Tools API ──

    /// <summary>Lists all available tools for the current model.</summary>
    public async Task<List<GitHub.Copilot.SDK.Rpc.Tool>> ListToolsAsync(string? model = null, CancellationToken ct = default)
    {
        if (_client is null) return [];
        var result = await _client.Rpc.Tools.ListAsync(model, ct);
        return result.Tools;
    }

    /// <summary>
    /// Launches the Copilot CLI login flow (OAuth device flow) and waits for completion.
    /// When <paramref name="onDeviceCode"/> is provided, stdout/stderr are captured to
    /// extract the one-time device code and verification URL, which are passed to the
    /// callback. The browser is opened automatically.
    /// When <paramref name="onDeviceCode"/> is null, falls back to UseShellExecute (legacy).
    /// </summary>
    public async Task<CopilotSignInResult> SignInAsync(
        Action<string, string>? onDeviceCode = null,
        CancellationToken ct = default)
    {
        var cliPath = FindCliPath();
        if (cliPath is null) return CopilotSignInResult.CliNotFound;

        if (onDeviceCode is null)
        {
            // Legacy path: let the CLI handle everything
            var legacyPsi = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "login",
                UseShellExecute = true,
            };
            using var legacyProcess = Process.Start(legacyPsi);
            if (legacyProcess is null) return CopilotSignInResult.Failed;
            await legacyProcess.WaitForExitAsync(ct);
            if (legacyProcess.ExitCode != 0) return CopilotSignInResult.Failed;
            await ConnectAsync(ct);
            return CopilotSignInResult.Success;
        }

        // Capture stdout/stderr to extract device code
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = "login",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return CopilotSignInResult.Failed;

        string? deviceCode = null;
        string? verificationUrl = null;
        int notified = 0; // 0 = not yet, 1 = done (atomic flag)
        var parseLock = new object();

        void ProcessLine(string line)
        {
            lock (parseLock)
            {
                ParseDeviceCodeLine(line, ref deviceCode, ref verificationUrl);

                if (deviceCode is not null && verificationUrl is not null
                    && Interlocked.CompareExchange(ref notified, 1, 0) == 0)
                {
                    var code = deviceCode;
                    var url = verificationUrl;
                    onDeviceCode(code, url);
                    OpenBrowser(url);
                }
            }
        }

        // Read both stdout and stderr — CLI may write to either
        var outputTask = Task.Run(async () =>
        {
            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                if (line is not null)
                    ProcessLine(line);
            }
        }, ct);

        var errorTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync(ct);
                if (line is not null)
                    ProcessLine(line);
            }
        }, ct);

        // Send Enter to proceed past "Press Enter to open..." prompt
        try
        {
            await Task.Delay(500, ct);
            await process.StandardInput.WriteLineAsync();
        }
        catch { /* best-effort */ }

        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            return CopilotSignInResult.Failed;

        await ConnectAsync(ct);
        return CopilotSignInResult.Success;
    }

    private static void ParseDeviceCodeLine(string line, ref string? deviceCode, ref string? verificationUrl)
    {
        // The CLI outputs lines like:
        //   "First copy your one-time code: XXXX-XXXX"
        //   "Open https://github.com/login/device in your browser"
        // or sometimes a combined message. We look for common patterns.

        // Look for a code pattern like ABCD-ABCD (4+ alphanum, dash, 4+ alphanum)
        if (deviceCode is null)
        {
            var codeMatch = System.Text.RegularExpressions.Regex.Match(
                line, @"\b([A-Z0-9]{4,}-[A-Z0-9]{4,})\b");
            if (codeMatch.Success)
                deviceCode = codeMatch.Groups[1].Value;
        }

        // Look for a URL containing "login/device" or "github.com"
        if (verificationUrl is null)
        {
            var urlMatch = System.Text.RegularExpressions.Regex.Match(
                line, @"(https?://\S+)");
            if (urlMatch.Success)
                verificationUrl = urlMatch.Groups[1].Value;
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Launches the Copilot CLI logout flow and reconnects without credentials.
    /// </summary>
    public async Task<bool> SignOutAsync(CancellationToken ct = default)
    {
        var cliPath = FindCliPath();
        if (cliPath is null) return false;

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = "logout",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return false;

        await process.WaitForExitAsync(ct);

        // Reconnect so the client picks up the new (unauthenticated) state
        await ForceReconnectAsync(ct);
        return true;
    }

    private static string? FindCliPath()
    {
        var binary = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "copilot.exe" : "copilot";
        var appDir = AppContext.BaseDirectory;

        // Check runtimes/{rid}/native/ (standard SDK output location)
        var rid = RuntimeInformation.RuntimeIdentifier;
        var runtimePath = Path.Combine(appDir, "runtimes", rid, "native", binary);
        if (File.Exists(runtimePath)) return runtimePath;

        // Fallback: try portable rid
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var portablePath = Path.Combine(appDir, "runtimes", $"{os}-{arch}", "native", binary);
        if (File.Exists(portablePath)) return portablePath;

        // Fallback: check app directory directly
        var directPath = Path.Combine(appDir, binary);
        if (File.Exists(directPath)) return directPath;

        return null;
    }

    private static void ConfigureAuthentication(CopilotClientOptions options)
    {
        var token = TryReadStoredGitHubToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            options.GitHubToken = token;
            options.UseLoggedInUser = false;
            return;
        }

        options.UseLoggedInUser = true;
    }

    private static string? TryReadStoredGitHubToken()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        try
        {
            var identity = GetStoredCopilotIdentity();
            if (string.IsNullOrWhiteSpace(identity.Login) || string.IsNullOrWhiteSpace(identity.Host))
                return null;

            var credentialBytes = ReadGenericCredential($"copilot-cli/{identity.Host}:{identity.Login}");
            return ExtractTokenFromCredential(credentialBytes);
        }
        catch
        {
            return null;
        }
    }

    private static (string? Login, string? Host) GetStoredCopilotIdentity()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot",
            "config.json");

        if (!File.Exists(configPath))
            return default;

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        if (!document.RootElement.TryGetProperty("last_logged_in_user", out var lastUser)
            || lastUser.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        return (
            lastUser.TryGetProperty("login", out var login) ? login.GetString() : null,
            lastUser.TryGetProperty("host", out var host) ? host.GetString() : null);
    }

    private static string? ExtractTokenFromCredential(byte[]? credentialBytes)
    {
        if (credentialBytes is not { Length: > 0 })
            return null;

        foreach (var decoded in new[]
        {
            Encoding.UTF8.GetString(credentialBytes).Trim('\0'),
            Encoding.Unicode.GetString(credentialBytes).Trim('\0'),
        })
        {
            if (string.IsNullOrWhiteSpace(decoded))
                continue;

            try
            {
                using var document = JsonDocument.Parse(decoded);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var propertyName in new[] { "access_token", "oauth_token", "token" })
                {
                    if (!document.RootElement.TryGetProperty(propertyName, out var property)
                        || property.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var token = property.GetString();
                    if (!string.IsNullOrWhiteSpace(token))
                        return token;
                }
            }
            catch
            {
                if (decoded.All(ch => ch >= ' ' && ch <= '~'))
                    return decoded;
            }
        }

        return null;
    }

    private static byte[]? ReadGenericCredential(string target)
    {
        if (!CredRead(target, 1, 0, out var credentialPtr))
            return null;

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                return [];

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    public async Task<string?> GenerateTitleAsync(string userMessage, CancellationToken ct = default)
    {
        if (_client is null) return null;

        var fastModel = await GetFastestModelIdAsync(ct).ConfigureAwait(false);

        // Embed the user message in the system message so the SDK's leaked
        // base context (tools, SQL tables, etc.) doesn't pollute the title.
        var systemContent = $"""
            Generate a short title (3-6 words) for a chat that starts with this message. Output ONLY the title text, nothing else.

            User: {Truncate(userMessage, 500)}
            """;

        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = fastModel,
            Streaming = false,
            SystemMessage = new SystemMessageConfig
            {
                Content = systemContent,
                Mode = SystemMessageMode.Replace
            },
            AvailableTools = [],
            ExcludedTools = ["*"],
            OnPermissionRequest = PermissionHandler.ApproveAll,
        }, ct).ConfigureAwait(false);

        try
        {
            var result = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = "title:" },
                TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            return result?.Data?.Content?.Trim().Trim('"', '\'', '.', '!');
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength];

    /// <summary>
    /// Creates a lightweight session with only the provided custom tools and a fully replaced
    /// system prompt. InfiniteSessions and skill directories are off. Built-in tools that could
    /// distract the model are excluded.
    /// </summary>
    public async Task<CopilotSession> CreateLightweightSessionAsync(
        string systemPrompt,
        string? model,
        List<AIFunction> tools,
        CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");

        // AvailableTools = only these tools are usable (whitelist, no built-in SDK tools)
        var toolNames = tools.Select(t => t.Name).ToList();

        var config = new SessionConfig
        {
            Model = model,
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Replace
            },
            Tools = tools,
            AvailableTools = toolNames,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        return await _client.CreateSessionAsync(config, ct);
    }

    public async Task<List<string>?> GenerateSuggestionsAsync(string assistantMessage, string? userMessage, CancellationToken ct = default)
    {
        if (_client is null) return null;

        var context = string.IsNullOrWhiteSpace(userMessage)
            ? assistantMessage
            : $"User: {userMessage}\n\nAssistant: {assistantMessage}";

        // Truncate to keep the request lightweight
        if (context.Length > 2000)
            context = context[..2000];

        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Content = "You generate follow-up suggestions for a chat assistant. Given the conversation below, produce exactly 3 short follow-up messages the user might want to send next. Each suggestion must be concise (under 60 characters) and contextually relevant. Output ONLY a JSON array of 3 strings, nothing else. Example: [\"Tell me more\", \"How do I implement this?\", \"What are the alternatives?\"]",
                Mode = SystemMessageMode.Replace
            },
            AvailableTools = [],
            OnPermissionRequest = PermissionHandler.ApproveAll,
        }, ct);

        try
        {
            var result = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = context },
                TimeSpan.FromSeconds(15), ct);
            var raw = result?.Data?.Content?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // Strip markdown code fences if present (e.g., ```json ... ```)
            if (raw.StartsWith("```"))
            {
                var firstNewline = raw.IndexOf('\n');
                if (firstNewline > 0) raw = raw[(firstNewline + 1)..];
                if (raw.EndsWith("```")) raw = raw[..^3];
                raw = raw.Trim();
            }

            // Parse the JSON array
            var suggestions = System.Text.Json.JsonSerializer.Deserialize(
                raw, Lumi.Models.AppDataJsonContext.Default.ListString);
            return suggestions?.Count > 0 ? suggestions : null;
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (_client is null) return;
        try { await _client.DeleteSessionAsync(sessionId, ct); }
        catch { /* Best-effort cleanup */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
