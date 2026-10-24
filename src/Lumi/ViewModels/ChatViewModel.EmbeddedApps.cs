using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.EmbeddedApps;

namespace Lumi.ViewModels;

/// <summary>
/// MCP Apps and Copilot canvases: the apps this chat's tools opened, the one the Workspace shows, and
/// what an open app asks of the chat's Copilot session.
/// </summary>
public partial class ChatViewModel
{
    private const int MaximumAppContextChars = 16 * 1024;
    private static readonly TimeSpan CanvasRegistrationTimeout = TimeSpan.FromSeconds(20);
    private readonly Dictionary<ChatMessage, EmbeddedAppItem> _appItems = [];

#if DEBUG
    internal CopilotSession? DebugAppSession => CurrentChat is { } chat
        && _sessionCache.TryGetValue(chat.Id, out var session) ? session : null;
#endif

    /// <summary>The chat's apps, newest first (the Workspace's "Apps &amp; canvases").</summary>
    public ObservableCollection<EmbeddedAppItem> AppItems { get; } = [];

    public WorkspaceSection<EmbeddedAppItem> WorkspaceApps { get; } = new(WorkspaceCategory.Apps, 3,
        static (item, query) => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>The app the Workspace's app page shows, or last showed.</summary>
    [ObservableProperty]
    private EmbeddedAppItem? _selectedApp;

    public event Action<EmbeddedAppItem>? AppShowRequested;
    public event Action? AppHideRequested;

    partial void OnSelectedAppChanged(EmbeddedAppItem? value) => UpdateShowingApp();

    partial void OnWorkspacePageChanged(WorkspacePage value) => UpdateShowingApp();

    private void UpdateShowingApp()
    {
        foreach (var item in _appItems.Values)
            item.IsShowing = ReferenceEquals(item, SelectedApp) && WorkspacePage == WorkspacePage.App;
    }

    /// <summary>The one item for a message's app, shared by its transcript card and its Workspace row.</summary>
    internal EmbeddedAppItem? GetAppItem(ChatMessage message)
    {
        if (message.EmbeddedApp is null)
            return null;
        if (!_appItems.TryGetValue(message, out var item))
        {
            _appItems[message] = item = new EmbeddedAppItem(message, OpenApp);
            item.IsShowing = ReferenceEquals(item, SelectedApp) && WorkspacePage == WorkspacePage.App;
        }
        return item;
    }

    private void RefreshAppItems()
    {
        // Every app of the chat, older exchanges' included, is in memory while it is loaded.
        var apps = CurrentChat is { } chat ? _dataStore.GetAppMessages(chat) : [];
        var desired = new List<EmbeddedAppItem>();
        for (var index = apps.Count - 1; index >= 0; index--)
        {
            if (GetAppItem(apps[index]) is { } item)
                desired.Add(item);
        }

        var kept = desired.Select(static item => item.Message).ToHashSet();
        foreach (var stale in _appItems.Keys.Where(message => !kept.Contains(message)).ToList())
            _appItems.Remove(stale);

        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            var current = AppItems.IndexOf(item);
            if (current < 0)
                AppItems.Insert(index, item);
            else if (current != index)
                AppItems.Move(current, index);
            item.Refresh();
        }
        while (AppItems.Count > desired.Count)
            AppItems.RemoveAt(AppItems.Count - 1);

        // An edited or regenerated turn can take the app on screen with it.
        if (SelectedApp is not null && !kept.Contains(SelectedApp.Message))
            CloseApp();
        WorkspaceApps.SetItems(AppItems);
    }

    /// <summary>Shows an app in the Workspace, from its card, its Workspace row, or the tool that opened it.</summary>
    private void OpenApp(EmbeddedAppItem item)
    {
        if (CurrentChat is not { } chat || !HoldsApp(chat, item.Message))
        {
            StatusText = Loc.Apps_Unavailable;
            return;
        }
        SelectedApp = item;
        AppShowRequested?.Invoke(item);
    }

    /// <summary>Whether <paramref name="message"/> holds one of the chat's apps (see <see cref="DataStore.GetAppMessages"/>).</summary>
    private bool HoldsApp(Chat chat, ChatMessage message) => _dataStore.GetAppMessages(chat).Contains(message);

    /// <summary>Takes the app page down when its chat leaves the screen.</summary>
    private void CloseApp()
    {
        if (SelectedApp is null)
            return;
        SelectedApp = null;
        AppHideRequested?.Invoke();
    }

    /// <summary>Puts a message's app card in the transcript and its row in the Workspace.</summary>
    private void ShowAppInChat(ChatMessage message)
    {
        // An app of an older exchange has its card there, built with its exchange.
        if (CurrentChat?.Messages.Contains(message) == true)
            _transcriptBuilder.ShowApp(message);
        else
            GetAppItem(message)?.Refresh();
        RebuildWorkspacePanel();
    }

    /// <summary>
    /// What the viewer loads for an app. The chat's session is live before the app starts (resumed if
    /// needed): a canvas is opened through it, which focuses a running instance or starts it again and
    /// returns the address its extension serves now, and an MCP App's interface is read from its connector.
    /// </summary>
    internal async Task<EmbeddedAppLaunch> PrepareAppAsync(Chat chat, ChatMessage message, CancellationToken cancellationToken)
    {
        var app = message.EmbeddedApp ?? throw new InvalidOperationException(Loc.Apps_Unavailable);
        var session = await GetAppSessionAsync(chat, message, cancellationToken);
        if (app.Kind == EmbeddedAppKind.Canvas)
        {
            await WaitForCanvasAsync(session, app, cancellationToken);
            var opened = await session.Rpc.Canvas.OpenAsync(app.CanvasId!, app.InstanceId!, app.ExtensionId,
                EmbeddedAppData.ParseJson(app.InputJson), cancellationToken);
            EmbeddedAppData.RecordOpenCanvas(chat, _dataStore.GetAppMessages(chat), session.SessionId, opened);
            QueueSaveChat(chat, saveIndex: false);
            GetAppItem(message)?.Refresh();
            return new EmbeddedAppLaunch(app.Kind, app.Title,
                Url: opened.Url ?? throw new InvalidOperationException(Loc.Apps_CanvasNoInterface));
        }

        if (app.Html is null)
        {
            var resource = await session.Rpc.Mcp.Apps.ReadResourceAsync(app.ServerName!, app.ResourceUri!, cancellationToken);
            var content = resource.Contents.FirstOrDefault(content => content.Uri == app.ResourceUri)
                ?? throw new InvalidOperationException(Loc.Apps_ConnectorNoInterface);
            app.MetadataJson = content.Meta is null ? null : EmbeddedAppData.ToJsonObject(content.Meta).ToJsonString();
            app.Html = EmbeddedAppData.ReadHtml(content.MimeType, content.Text, content.Blob);
        }
        return new EmbeddedAppLaunch(app.Kind, app.Title, Html: app.Html, MetadataJson: app.MetadataJson,
            InputJson: app.InputJson, ResultJson: app.ResultJson);
    }

    /// <summary>
    /// The chat's session for an app: its saved session, resumed if needed (never a new one). An open
    /// app's requests take the live session as it is; opening an app checks that it still answers.
    /// </summary>
    private async Task<CopilotSession> GetAppSessionAsync(Chat chat, ChatMessage message,
        CancellationToken cancellationToken, bool reuseLive = false)
    {
        if (_isDisposed || !HoldsApp(chat, message))
            throw new InvalidOperationException(Loc.Apps_Unavailable);
        if (reuseLive && _sessionCache.TryGetValue(chat.Id, out var live) && live.SessionId == chat.CopilotSessionId)
        {
            GetOrCreateRuntimeState(chat.Id).LastSessionActivityUtc = DateTimeOffset.UtcNow;
            return live;
        }
        return await GetOrResumeSessionAsync(chat, cancellationToken)
            ?? throw new InvalidOperationException(Loc.Apps_NoSession);
    }

    /// <summary>
    /// A resumed session starts its extensions in the background, and a canvas opens only once its
    /// extension has registered it.
    /// </summary>
    private static async Task WaitForCanvasAsync(CopilotSession session, EmbeddedApp app, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (!(await session.Rpc.Canvas.ListAsync(cancellationToken)).Canvases
                   .Any(canvas => canvas.CanvasId == app.CanvasId && canvas.ExtensionId == app.ExtensionId))
        {
            if (waited.Elapsed > CanvasRegistrationTimeout)
                throw new InvalidOperationException(string.Format(Loc.Apps_ExtensionUnavailable, app.SourceName ?? app.ExtensionId));
            await Task.Delay(500, cancellationToken);
        }
    }

    /// <summary>Handles a request an open MCP App makes through the bridge (called off the UI thread).</summary>
    internal async Task<JsonNode?> DispatchAppRequestAsync(Chat chat, ChatMessage message, string method,
        JsonElement parameters, CancellationToken cancellationToken)
    {
        var captured = parameters.ValueKind == JsonValueKind.Undefined ? parameters : parameters.Clone();
        return await Dispatcher.UIThread.InvokeAsync(
            () => HandleAppRequestAsync(chat, message, method, captured, cancellationToken));
    }

    private async Task<JsonNode?> HandleAppRequestAsync(Chat chat, ChatMessage message, string method,
        JsonElement parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isDisposed || !HoldsApp(chat, message)
            || message.EmbeddedApp is not { Kind: EmbeddedAppKind.McpApp, ServerName: { } serverName } app)
            throw new InvalidOperationException(Loc.Apps_Unavailable);

        switch (method)
        {
            case "tools/call":
            {
                var name = parameters.GetProperty("name").GetString()
                    ?? throw new FormatException(Loc.Apps_ToolNameMissing);
                var arguments = parameters.TryGetProperty("arguments", out var supplied) && supplied.ValueKind == JsonValueKind.Object
                    ? supplied.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value.Clone())
                    : null;
                var session = await GetAppSessionAsync(chat, message, cancellationToken, reuseLive: true);
                // The app cannot choose which connector it speaks for, including for its app-only tools.
                var result = await session.Rpc.Mcp.Apps.CallToolAsync(serverName, name, serverName, arguments, cancellationToken);
                return EmbeddedAppData.ToJsonObject(result);
            }
            case "resources/read":
            {
                var uri = parameters.GetProperty("uri").GetString()
                    ?? throw new FormatException(Loc.Apps_ResourceNameMissing);
                var session = await GetAppSessionAsync(chat, message, cancellationToken, reuseLive: true);
                return EmbeddedAppData.ToResourceResult(
                    await session.Rpc.Mcp.Apps.ReadResourceAsync(serverName, uri, cancellationToken));
            }
            case "ui/message":
            {
                if (parameters.GetProperty("role").GetString() != "user")
                    throw new InvalidOperationException(Loc.Apps_UserMessagesOnly);
                var text = string.Join("\n", parameters.GetProperty("content").EnumerateArray().Select(block =>
                    block.GetProperty("type").GetString() == "text" ? block.GetProperty("text").GetString()
                        : throw new InvalidOperationException(Loc.Apps_TextMessagesOnly)));
                if (string.IsNullOrWhiteSpace(text))
                    throw new FormatException(Loc.Apps_MessageEmpty);
                // The message is labelled with the app that sent it. Its turn outlives the viewer.
                var started = await StartExternalMessageAsync(chat, text, app.Title);
                if (!started.Accepted)
                    throw new InvalidOperationException(started.Error ?? Loc.Apps_RequestFailed);
                return new JsonObject();
            }
            case "ui/update-model-context":
                if (parameters.GetRawText().Length > MaximumAppContextChars)
                    throw new InvalidOperationException(Loc.Apps_ContextTooLarge);
                app.ModelContextJson = parameters.GetRawText();
                message.MarkChanged();
                QueueSaveChat(chat, saveIndex: false);
                return new JsonObject();
            case "ui/open-link":
            {
                var url = EmbeddedAppHost.ValidateWebUrl(parameters.GetProperty("url").GetString());
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
                return new JsonObject();
            }
            case "log":
                Trace.TraceInformation("[EmbeddedApp {0}] {1}", app.Title, parameters.GetRawText());
                return new JsonObject();
            default:
                throw new InvalidOperationException(Loc.Get(nameof(Loc.Apps_UnsupportedOperation), method));
        }
    }

    /// <summary>
    /// Keeps the app a completed connector tool supplied on its message and, in the chat on screen,
    /// shows it. Without Copilot's MCP Apps support, the tool's text result stands in for it.
    /// </summary>
    private void RecordMcpApp(Chat chat, CopilotSession session, ChatMessage toolMessage,
        ToolExecutionCompleteData completed, string? serverName, bool isDisplayed)
    {
        try
        {
            if (session.Capabilities.Ui?.McpApps != true)
            {
                if (completed.Result?.UiResource is not null || completed.ToolDescription?.Meta?.Ui?.ResourceUri is not null)
                {
                    toolMessage.ToolOutput = Loc.Apps_RuntimeUnavailable + "\n" + completed.Result?.Content;
                    QueueSaveChat(chat, saveIndex: false);
                }
                return;
            }
            var displayName = serverName is null ? null : ResolveMcpDisplayName(chat.Id, serverName);
            if (EmbeddedAppData.CaptureMcpApp(serverName, displayName, toolMessage, completed) is not { } app)
                return;
            toolMessage.EmbeddedApp = app;
            QueueSaveChat(chat, saveIndex: false);
            if (isDisplayed)
            {
                ShowAppInChat(toolMessage);
                OpenApp(GetAppItem(toolMessage)!);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or JsonException)
        {
            Trace.TraceWarning("[EmbeddedApp] Could not keep the tool's app: {0}", error);
            if (isDisplayed)
                StatusText = error.Message;
        }
    }

    private void OnCanvasOpened(Chat chat, string sessionId, OpenCanvasInstance canvas, bool show)
    {
        // A canvas that starts is shown; one a resumed session restores, or the agent focuses, stays where it was.
        var apps = _dataStore.GetAppMessages(chat);
        var wasOpen = apps.Any(message => message.EmbeddedApp is { IsClosed: false } app && app.InstanceId == canvas.InstanceId);
        if (EmbeddedAppData.RecordOpenCanvas(chat, apps, sessionId, canvas) is not { } message)
        {
            Trace.TraceInformation("[EmbeddedApp] Canvas '{0}' was not opened by a tool call in this chat.", canvas.InstanceId);
            return;
        }
        QueueSaveChat(chat, saveIndex: false);
        if (_isDisposed || CurrentChat?.Id != chat.Id)
            return;
        ShowAppInChat(message);
        if (show && !wasOpen && GetAppItem(message) is { IsShowing: false } item)
            OpenApp(item);
    }

    private void OnCanvasClosed(Chat chat, string instanceId)
    {
        if (EmbeddedAppData.CloseCanvas(_dataStore.GetAppMessages(chat), instanceId) is not { } message)
            return;
        QueueSaveChat(chat, saveIndex: false);
        if (_isDisposed || CurrentChat?.Id != chat.Id || !_appItems.TryGetValue(message, out var item))
            return;
        item.Refresh();
        if (ReferenceEquals(item, SelectedApp))
            AppHideRequested?.Invoke();
    }

    /// <summary>Picks up the canvases a resumed session reopened (their extensions serve new addresses).</summary>
    private async Task ReconcileOpenCanvasesAsync(CopilotSession session, Chat chat)
    {
        if (!_dataStore.GetAppMessages(chat).Any(static message => message.EmbeddedApp?.Kind == EmbeddedAppKind.Canvas))
            return;
        try
        {
            var open = await session.Rpc.Canvas.ListOpenAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isDisposed || !_sessionCache.TryGetValue(chat.Id, out var current) || !ReferenceEquals(current, session))
                    return;
                foreach (var canvas in open.OpenCanvases)
                    OnCanvasOpened(chat, session.SessionId, canvas, show: false);
            });
        }
        catch (Exception error)
        {
            Trace.TraceWarning("[EmbeddedApp] Could not list open canvases: {0}", error);
        }
    }

    [RelayCommand]
    private async Task CloseSelectedCanvas()
    {
        if (CurrentChat is not { } chat
            || SelectedApp is not { Message.EmbeddedApp: { Kind: EmbeddedAppKind.Canvas, IsClosed: false, InstanceId: { } instanceId } } item)
            return;
        try
        {
            var session = await GetAppSessionAsync(chat, item.Message, CancellationToken.None);
            await session.Rpc.Canvas.CloseAsync(instanceId);
            OnCanvasClosed(chat, instanceId);
        }
        catch (Exception error)
        {
            Trace.TraceWarning("[EmbeddedApp] Could not close the canvas: {0}", error);
            StatusText = error.Message;
        }
    }
}
