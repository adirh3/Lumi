using System;
using System.Collections.Generic;
using System.Linq;
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
    public event Action<string, bool>? OnToolComplete;         // toolCallId, success
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
                    OnToolComplete?.Invoke(toolEnd.Data.ToolCallId, toolEnd.Data.Success == true);
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
