using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Mcp;

public sealed class LumiMcpToolHandlers
{
    private static readonly List<Process> LaunchedProcesses = [];
    private readonly LumiDebugBridgeClient _bridgeClient = new();
    private readonly string _repositoryRoot;

    public LumiMcpToolHandlers(string? repositoryRoot = null)
    {
        _repositoryRoot = repositoryRoot ?? FindRepositoryRoot();
    }

    public async Task<object?> HandleAsync(string name, JsonElement? arguments, CancellationToken cancellationToken)
    {
        return name switch
        {
            "lumi_status" => await _bridgeClient.GetStatusOrOfflineAsync(arguments, cancellationToken).ConfigureAwait(false),
            "lumi_list_instances" => ListInstances(arguments),
            "lumi_launch" => await LaunchAsync(arguments, cancellationToken).ConfigureAwait(false),
            "lumi_run_harness" => await RunHarnessAsync(arguments, cancellationToken).ConfigureAwait(false),
            "lumi_navigate" => await _bridgeClient.InvokeAsync("navigate", arguments, cancellationToken).ConfigureAwait(false),
            "lumi_list_chats" => await _bridgeClient.InvokeAsync("list_chats", arguments, cancellationToken).ConfigureAwait(false),
            "lumi_create_chat" => await _bridgeClient.InvokeAsync("create_chat", arguments, cancellationToken).ConfigureAwait(false),
            "lumi_open_chat" => await _bridgeClient.InvokeAsync("open_chat", arguments, cancellationToken).ConfigureAwait(false),
            "lumi_send_message" => await _bridgeClient.InvokeAsync("send_message", arguments, cancellationToken).ConfigureAwait(false),
            "lumi_wait_for_idle" => await _bridgeClient.InvokeAsync("wait_for_idle", arguments, cancellationToken).ConfigureAwait(false),
            "lumi_read_transcript" => await _bridgeClient.InvokeAsync("read_transcript", arguments, cancellationToken).ConfigureAwait(false),
            "lumi_read_activity" => await _bridgeClient.InvokeAsync("read_activity", arguments, cancellationToken).ConfigureAwait(false),
            "lumi_load_fixture" => await _bridgeClient.InvokeAsync("load_fixture", arguments, cancellationToken).ConfigureAwait(false),
            "lumi_list_features" => await _bridgeClient.InvokeAsync("list_features", arguments, cancellationToken).ConfigureAwait(false),
            "lumi_configure_feature" => await _bridgeClient.InvokeAsync("configure_feature", arguments, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown Lumi MCP tool '{name}'.")
        };
    }

    private async Task<object> LaunchAsync(JsonElement? arguments, CancellationToken cancellationToken)
    {
        var args = arguments ?? default;
        var timeoutMs = GetInt(args, "timeoutMs") ?? 90000;
        var appDataDir = GetString(args, "appDataDir");
        var reuseExisting = GetBool(args, "reuseExisting") ?? string.IsNullOrWhiteSpace(appDataDir);
        if (reuseExisting)
        {
            var existing = await _bridgeClient.GetStatusOrOfflineAsync(args, cancellationToken).ConfigureAwait(false);
            if (IsBridgeAvailable(existing))
                return existing;
        }

        var harness = (GetString(args, "harness") ?? "fixture").Trim().ToLowerInvariant();
        var lumiProject = Path.Combine(_repositoryRoot, "src", "Lumi", "Lumi.csproj");
        if (!File.Exists(lumiProject))
            throw new FileNotFoundException("Could not find Lumi.csproj. Set the MCP server working directory to the Lumi repo root.", lumiProject);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = _repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        if (!string.IsNullOrWhiteSpace(appDataDir))
        {
            Directory.CreateDirectory(appDataDir);
            process.StartInfo.Environment["LUMI_APPDATA_DIR"] = Path.GetFullPath(appDataDir);
        }
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add(lumiProject);
        process.StartInfo.ArgumentList.Add("--configuration");
        process.StartInfo.ArgumentList.Add("Debug");
        process.StartInfo.ArgumentList.Add("--");
        if (harness is "fixture" or "debug" or "debug-agent-harness")
            process.StartInfo.ArgumentList.Add("--debug-agent-harness");

        var outputLines = new ConcurrentQueue<string>();
        var errorLines = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) outputLines.Enqueue(eventArgs.Data); };
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) errorLines.Enqueue(eventArgs.Data); };

        var launchStartedAt = DateTimeOffset.Now.AddSeconds(-2);
        TrackLaunchedProcess(process);
        if (!process.Start())
        {
            UntrackLaunchedProcess(process);
            throw new InvalidOperationException("Failed to start dotnet run for Lumi.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        TrackLaunchedProcess(process);

        var status = await WaitForBridgeAsync(args, timeoutMs, launchStartedAt, cancellationToken).ConfigureAwait(false);
        var launchStatus = ExtractLaunchStatus(status);
        return new
        {
            launched = true,
            launcherProcessId = process.Id,
            appProcessId = launchStatus.AppProcessId,
            bridgeProcessId = launchStatus.BridgeProcessId,
            bridgeInstanceId = launchStatus.InstanceId,
            bridgeUrl = launchStatus.Url,
            harness,
            appDataDir = launchStatus.AppDataDir ?? (string.IsNullOrWhiteSpace(appDataDir) ? null : Path.GetFullPath(appDataDir)),
            startupOutputPreview = StartupPreview(outputLines),
            startupErrorPreview = StartupPreview(errorLines),
            status
        };
    }

    private object ListInstances(JsonElement? arguments)
    {
        var args = arguments ?? default;
        var includeOffline = GetBool(args, "includeOffline") ?? false;
        var appDataDir = GetString(args, "appDataDir") ?? GetString(args, "targetAppDataDir");
        var instances = _bridgeClient.EnumerateDiscoveries()
            .Select(discovery => new
            {
                instanceId = discovery.InstanceId,
                appProcessId = discovery.ProcessId,
                bridgeProcessId = discovery.ProcessId,
                bridgeUrl = discovery.Url,
                discovery.StartedAt,
                discovery.AppDataDir,
                discovery.SourcePath,
                isAlive = LumiDebugBridgeClient.IsProcessAlive(discovery.ProcessId)
            })
            .Where(instance => includeOffline || instance.isAlive);

        if (!string.IsNullOrWhiteSpace(appDataDir))
        {
            var normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(appDataDir));
            instances = instances.Where(instance =>
                LumiDebugBridgeClient.AppDataMatches(instance.AppDataDir, normalized));
        }

        var list = instances
            .OrderByDescending(instance => instance.StartedAt)
            .ToList();
        return new
        {
            total = list.Count,
            instances = list
        };
    }

    private async Task<object> RunHarnessAsync(JsonElement? arguments, CancellationToken cancellationToken)
    {
        var args = arguments ?? default;
        var kind = RequireString(args, "kind").Trim().ToLowerInvariant();
        var flag = kind switch
        {
            "chat" or "chat-stress" => "--test-chat-stress",
            "mcp-native" or "native" => "--test-mcp-native",
            "mcp-proxy" or "proxy" => "--test-mcp-proxy",
            _ => throw new InvalidOperationException("Harness kind must be chat, mcp-native, or mcp-proxy.")
        };
        var timeoutMs = GetInt(args, "timeoutMs") ?? 180000;
        var includeStdout = GetBool(args, "includeStdout") ?? true;
        var includeStderr = GetBool(args, "includeStderr") ?? true;
        var maxOutputChars = Math.Clamp(GetInt(args, "maxOutputChars") ?? 12000, 0, 200000);
        var expectedOutput = GetString(args, "expectedOutput");
        var lumiProject = Path.Combine(_repositoryRoot, "src", "Lumi", "Lumi.csproj");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = _repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add(lumiProject);
        process.StartInfo.ArgumentList.Add("--configuration");
        process.StartInfo.ArgumentList.Add("Debug");
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(flag);

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start Lumi harness {kind}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw new TimeoutException($"Lumi harness {kind} did not finish within {timeoutMs} ms.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var expectedFound = string.IsNullOrWhiteSpace(expectedOutput)
            ? (bool?)null
            : stdout.Contains(expectedOutput, StringComparison.Ordinal)
              || stderr.Contains(expectedOutput, StringComparison.Ordinal);

        return new
        {
            kind,
            flag,
            process.ExitCode,
            expectedOutput,
            expectedFound,
            stdout = includeStdout ? Preview(stdout, maxOutputChars) : null,
            stderr = includeStderr ? Preview(stderr, maxOutputChars) : null
        };
    }

    private async Task<object> WaitForBridgeAsync(
        JsonElement args,
        int timeoutMs,
        DateTimeOffset? launchedAfter,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds <= timeoutMs)
        {
            if (launchedAfter.HasValue)
            {
                var appDataDir = GetString(args, "targetAppDataDir") ?? GetString(args, "appDataDir");
                var candidates = _bridgeClient.EnumerateDiscoveries()
                    .Where(discovery => LumiDebugBridgeClient.IsProcessAlive(discovery.ProcessId))
                    .Where(discovery => discovery.StartedAt >= launchedAfter.Value);
                if (!string.IsNullOrWhiteSpace(appDataDir))
                {
                    var normalizedAppDataDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(appDataDir));
                    candidates = candidates.Where(discovery =>
                        LumiDebugBridgeClient.AppDataMatches(discovery.AppDataDir, normalizedAppDataDir));
                }

                var discovery = candidates.OrderByDescending(candidate => candidate.StartedAt).FirstOrDefault();
                if (discovery is not null)
                {
                    var candidateStatus = await _bridgeClient.GetStatusForDiscoveryAsync(discovery, cancellationToken).ConfigureAwait(false);
                    if (IsBridgeAvailable(candidateStatus))
                        return candidateStatus;
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var status = await _bridgeClient.GetStatusOrOfflineAsync(args, cancellationToken).ConfigureAwait(false);
            if (IsBridgeAvailable(status))
                return status;

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Lumi debug bridge did not become available within {timeoutMs} ms.");
    }

    private static LaunchStatus ExtractLaunchStatus(object status)
    {
        using var document = JsonSerializer.SerializeToDocument(status);
        var root = document.RootElement;
        var instanceId = TryGetString(root, "instanceId")
                         ?? TryGetString(root, "discovery", "instanceId")
                         ?? TryGetString(root, "status", "bridge", "instanceId");
        var processId = TryGetInt(root, "appProcessId")
                        ?? TryGetInt(root, "bridgeProcessId")
                        ?? TryGetInt(root, "discovery", "processId")
                        ?? TryGetInt(root, "status", "bridge", "processId");
        var url = TryGetString(root, "bridgeUrl")
                  ?? TryGetString(root, "discovery", "url")
                  ?? TryGetString(root, "status", "bridge", "url");
        var appDataDir = TryGetString(root, "appDataDir")
                         ?? TryGetString(root, "discovery", "appDataDir")
                         ?? TryGetString(root, "status", "bridge", "appDataDir");
        return new LaunchStatus(instanceId, processId, processId, url, appDataDir);
    }

    private static string StartupPreview(IEnumerable<string> lines)
    {
        var filtered = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.Contains("warning NU190", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Contains("NETSDK1057", StringComparison.OrdinalIgnoreCase))
            .TakeLast(20);
        return string.Join(Environment.NewLine, filtered);
    }

    private static string? TryGetString(JsonElement root, params string[] path)
    {
        return TryGetProperty(root, path, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? TryGetInt(JsonElement root, params string[] path)
    {
        if (!TryGetProperty(root, path, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static bool TryGetProperty(JsonElement root, string[] path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }

        return true;
    }

    private sealed record LaunchStatus(
        string? InstanceId,
        int? AppProcessId,
        int? BridgeProcessId,
        string? Url,
        string? AppDataDir);

    private static void TrackLaunchedProcess(Process process)
    {
        lock (LaunchedProcesses)
            LaunchedProcesses.RemoveAll(static candidate => SafeHasExited(candidate));

        process.Exited += (_, _) =>
            UntrackLaunchedProcess(process);

        lock (LaunchedProcesses)
            LaunchedProcesses.Add(process);
        process.EnableRaisingEvents = true;
    }

    private static void UntrackLaunchedProcess(Process process)
    {
        lock (LaunchedProcesses)
        {
            LaunchedProcesses.Remove(process);
            process.Dispose();
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool IsBridgeAvailable(object status)
    {
        using var document = JsonSerializer.SerializeToDocument(status);
        return document.RootElement.TryGetProperty("bridgeAvailable", out var available)
            && available.ValueKind == JsonValueKind.True;
    }

    private static string FindRepositoryRoot()
    {
        var candidates = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var candidate in candidates)
        {
            var directory = new DirectoryInfo(candidate);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Lumi.slnx"))
                    && File.Exists(Path.Combine(directory.FullName, "src", "Lumi", "Lumi.csproj")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        return Environment.CurrentDirectory;
    }

    private static string RequireString(JsonElement args, string propertyName)
        => GetString(args, propertyName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{propertyName} is required.");

    private static string? GetString(JsonElement args, string propertyName)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool? GetBool(JsonElement args, string propertyName)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? GetInt(JsonElement args, string propertyName)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;

        return null;
    }

    private static string? Preview(string? value, int maxLength)
    {
        if (value is null || maxLength <= 0)
            return null;

        var normalized = value.Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..Math.Max(0, maxLength - 1)].TrimEnd() + "...";
    }
}
