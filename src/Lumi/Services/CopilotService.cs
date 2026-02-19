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
    private CopilotSession? _session;
    private string? _currentSessionId;
    private List<ModelInfo>? _models;

    public bool IsConnected => _client?.State == ConnectionState.Connected;
    public string? CurrentSessionId => _currentSessionId;

    public event Action<string>? OnMessageDelta;
    public event Action<string>? OnMessageComplete;
    public event Action<string, string>? OnReasoningDelta;   // reasoningId, delta
    public event Action<string>? OnReasoningComplete;
    public event Action<string, string, string?>? OnToolStart; // toolCallId, toolName, args
    public event Action<string, bool, ToolExecutionCompleteDataResult?>? OnToolComplete; // toolCallId, success, result
    public event Action? OnTurnStart;
    public event Action? OnTurnEnd;
    public event Action<string>? OnTitleChanged;
    public event Action<string>? OnError;
    public event Action? OnIdle;
    public event Action<string>? OnSkillInvoked;               // skillName
    public event Action<string>? OnSubagentStarted;            // agentName
    public event Action<string>? OnSubagentCompleted;          // agentName

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
        CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");

        var config = new SessionConfig
        {
            Model = model,
            Streaming = true,
            WorkingDirectory = workingDirectory,
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

        _session = await _client.CreateSessionAsync(config, ct);
        _currentSessionId = _session.SessionId;
        SubscribeToEvents(_session);
        return _session;
    }

    public async Task<CopilotSession> ResumeSessionAsync(
        string sessionId,
        string? systemPrompt = null,
        string? model = null,
        string? workingDirectory = null,
        List<string>? skillDirectories = null,
        List<CustomAgentConfig>? customAgents = null,
        List<AIFunction>? tools = null,
        CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");

        var config = new ResumeSessionConfig
        {
            Model = model,
            Streaming = true,
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

        _session = await _client.ResumeSessionAsync(sessionId, config, ct);
        _currentSessionId = _session.SessionId;
        SubscribeToEvents(_session);
        return _session;
    }

    public async Task SendMessageAsync(string prompt, List<UserMessageDataAttachmentsItemFile>? attachments = null, CancellationToken ct = default)
    {
        if (_session is null) throw new InvalidOperationException("No active session");

        var options = new MessageOptions { Prompt = prompt };
        if (attachments is { Count: > 0 })
        {
            options.Attachments = attachments
                .Cast<UserMessageDataAttachmentsItem>()
                .ToList();
        }

        await _session.SendAsync(options, ct);
    }

    public async Task AbortAsync(CancellationToken ct = default)
    {
        if (_session is not null)
            await _session.AbortAsync(ct);
    }

    private void SubscribeToEvents(CopilotSession session)
    {
        session.On(evt =>
        {
            switch (evt)
            {
                case AssistantTurnStartEvent:
                    OnTurnStart?.Invoke();
                    break;
                case AssistantMessageDeltaEvent delta:
                    OnMessageDelta?.Invoke(delta.Data.DeltaContent);
                    break;
                case AssistantMessageEvent msg:
                    OnMessageComplete?.Invoke(msg.Data.Content);
                    break;
                case AssistantReasoningDeltaEvent rd:
                    OnReasoningDelta?.Invoke(rd.Data.ReasoningId, rd.Data.DeltaContent);
                    break;
                case AssistantReasoningEvent r:
                    OnReasoningComplete?.Invoke(r.Data.Content);
                    break;
                case ToolExecutionStartEvent toolStart:
                    OnToolStart?.Invoke(toolStart.Data.ToolCallId, toolStart.Data.ToolName, toolStart.Data.Arguments?.ToString());
                    break;
                case ToolExecutionCompleteEvent toolEnd:
                    OnToolComplete?.Invoke(toolEnd.Data.ToolCallId, toolEnd.Data.Success == true, toolEnd.Data.Result);
                    break;
                case AssistantTurnEndEvent:
                    OnTurnEnd?.Invoke();
                    break;
                case SessionTitleChangedEvent title:
                    OnTitleChanged?.Invoke(title.Data.Title);
                    break;
                case SessionIdleEvent:
                    OnIdle?.Invoke();
                    break;
                case SessionErrorEvent err:
                    OnError?.Invoke(err.Data.Message);
                    break;
                case SkillInvokedEvent skill:
                    OnSkillInvoked?.Invoke(skill.Data.Name);
                    break;
                case SubagentStartedEvent subStart:
                    OnSubagentStarted?.Invoke(subStart.Data.AgentDisplayName);
                    break;
                case SubagentCompletedEvent subEnd:
                    OnSubagentCompleted?.Invoke(subEnd.Data.AgentDisplayName);
                    break;
            }
        });
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

    public async Task<List<SessionMetadata>> ListSessionsAsync(CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("Not connected");
        return await _client.ListSessionsAsync(cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
            await _session.DisposeAsync();
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
