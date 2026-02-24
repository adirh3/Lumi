using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot.SDK;
using Lumi.Models;
using Microsoft.Extensions.AI;

namespace Lumi.Services;

public class CopilotService : IAsyncDisposable
{
    private CopilotClient? _client;
    private List<ModelInfo>? _models;

    public bool IsConnected => _client?.State == ConnectionState.Connected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _client = new CopilotClient(new CopilotClientOptions
        {
            LogLevel = "error"
        });

        await _client.StartAsync(ct);
    }

    public async Task<List<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        _models ??= await _client.ListModelsAsync(ct);
        return _models;
    }

    public async Task<CopilotSession> CreateSessionAsync(
        string? systemPrompt = null,
        string? model = null,
        string? workingDirectory = null,
        List<string>? skillDirectories = null,
        List<CustomAgentConfig>? customAgents = null,
        List<AIFunction>? tools = null,
        Dictionary<string, object>? mcpServers = null,
        CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");

        var config = new SessionConfig
        {
            Model = model,
            Streaming = true,
            WorkingDirectory = workingDirectory,
            ExcludedTools = ["web_fetch", "web_search"],
        };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Append
            };
        }

        if (skillDirectories is { Count: > 0 })
            config.SkillDirectories = skillDirectories;

        if (customAgents is { Count: > 0 })
            config.CustomAgents = customAgents;

        if (tools is { Count: > 0 })
            config.Tools = tools;

        if (mcpServers is { Count: > 0 })
            config.McpServers = mcpServers;

        return await _client.CreateSessionAsync(config, ct);
    }

    public async Task<CopilotSession> ResumeSessionAsync(
        string sessionId,
        string? systemPrompt = null,
        string? model = null,
        string? workingDirectory = null,
        List<string>? skillDirectories = null,
        List<CustomAgentConfig>? customAgents = null,
        List<AIFunction>? tools = null,
        Dictionary<string, object>? mcpServers = null,
        CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");

        var config = new ResumeSessionConfig
        {
            Model = model,
            Streaming = true,
            ExcludedTools = ["web_fetch", "web_search"],
        };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Content = systemPrompt,
                Mode = SystemMessageMode.Append
            };
        }

        if (skillDirectories is { Count: > 0 })
            config.SkillDirectories = skillDirectories;

        if (customAgents is { Count: > 0 })
            config.CustomAgents = customAgents;

        if (tools is { Count: > 0 })
            config.Tools = tools;

        if (mcpServers is { Count: > 0 })
            config.McpServers = mcpServers;

        return await _client.ResumeSessionAsync(sessionId, config, ct);
    }

    public async Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.GetAuthStatusAsync(ct);
    }

    /// <summary>
    /// Launches the Copilot CLI login flow (OAuth device flow) and waits for completion.
    /// </summary>
    public async Task<bool> SignInAsync(CancellationToken ct = default)
    {
        var cliPath = FindCliPath();
        if (cliPath is null) return false;

        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = "login",
            UseShellExecute = true, // Opens browser for OAuth
        };

        using var process = Process.Start(psi);
        if (process is null) return false;

        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0;
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

    public async Task<string?> GenerateTitleAsync(string userMessage, CancellationToken ct = default)
    {
        if (_client is null) return null;

        var session = await _client.CreateSessionAsync(new SessionConfig
        {
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Content = "You are a title generator. Generate a concise title (3-6 words) for a chat conversation that started with the user message below. Respond with ONLY the title text, nothing else. No quotes, no punctuation at the end. Do not refuse or explain — just output the title.",
                Mode = SystemMessageMode.Replace
            },
            AvailableTools = []
        }, ct);

        try
        {
            var result = await session.SendAndWaitAsync(
                new MessageOptions { Prompt = userMessage },
                TimeSpan.FromSeconds(30), ct);
            return result?.Data?.Content?.Trim();
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    public async Task<List<SessionMetadata>> ListSessionsAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.ListSessionsAsync(cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
