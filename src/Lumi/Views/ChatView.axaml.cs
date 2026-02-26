using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class ChatView : UserControl
{
    private StrataChatShell? _chatShell;
    private StrataChatComposer? _welcomeComposer;
    private StrataChatComposer? _activeComposer;
    private StrataAttachmentList? _welcomePendingAttachmentList;
    private StrataAttachmentList? _pendingAttachmentList;
    private Panel? _welcomePanel;
    private Panel? _chatPanel;
    private StackPanel? _messageStack;
    private Panel? _dropOverlay;
    private Panel? _loadingOverlay;

    // Cancellation for in-progress async rebuilds (allows fast chat switching)
    private CancellationTokenSource? _rebuildCts;

    // Incremental loading: older messages not yet rendered
    private List<ChatMessageViewModel>? _deferredMessages;
    private bool _isLoadingOlder;
    private ScrollViewer? _transcriptScrollViewer;
    private int _transcriptBuildDepth;

    private bool IsTranscriptBuilding => _transcriptBuildDepth > 0;

    // Tool grouping state
    private StrataThink? _currentToolGroup;
    private StackPanel? _currentToolGroupStack;
    private int _currentToolGroupCount;
    private StrataAiToolCall? _currentTodoToolCall;
    private TodoProgressState? _currentTodoProgress;
    private int _todoUpdateCount;

    // Typing indicator
    private StrataTypingIndicator? _typingIndicator;

    // Track files already shown as chips to avoid duplicates
    private readonly HashSet<string> _shownFilePaths = new(StringComparer.OrdinalIgnoreCase);

    // Files created during tool execution, to be shown after the assistant message
    private readonly List<string> _pendingToolFileChips = [];

    // Search sources collected during tool execution, shown on the next assistant message
    private readonly List<SearchSource> _pendingSearchSources = [];

    // Skills fetched via fetch_skill tool, shown on the next assistant message
    private readonly List<SkillReference> _pendingFetchedSkills = [];

    // File edits collected during tool execution, shown as "Changes" section on the next assistant message
    private readonly List<(string FilePath, string ToolName, string? OldText, string? NewText)> _pendingFileEdits = [];

    // Track tool call start times for duration display
    private readonly Dictionary<string, long> _toolStartTimes = [];

    // Track the current intent text from report_intent for friendly group labels
    private string? _currentIntentText;

    // Track the active terminal preview for powershell tool output
    private StrataTerminalPreview? _activeTerminalPreview;
    private readonly Dictionary<string, StrataTerminalPreview> _terminalPreviewsByToolCallId = new(StringComparer.Ordinal);

    // Counter for naming message controls (Avalonia MCP automation)
    private int _messageCounter;

    private sealed class TodoStepSnapshot
    {
        public int Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Status { get; init; } = "not-started";
    }

    private sealed class TodoProgressState
    {
        public string ToolStatus { get; set; } = "InProgress";
        public int Total { get; set; }
        public int Completed { get; set; }
        public int Failed { get; set; }
    }

    // Track if we've already wired up event handlers
    private ChatViewModel? _subscribedVm;
    private SettingsViewModel? _settingsVm;

    // Voice input
    private readonly VoiceInputService _voiceService = new();
    private string _textBeforeVoice = "";       // prompt text snapshot before recording started
    private string _lastHypothesis = "";         // most recent partial text (replaced on each update)
    private bool _voiceStarting;                  // guard against re-entrant calls while starting

    public ChatView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsureSettingsSubscription();
        if (_subscribedVm is not null)
            ApplyRuntimeSettings(_subscribedVm);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _chatShell = this.FindControl<StrataChatShell>("ChatShell");
        _welcomeComposer = this.FindControl<StrataChatComposer>("WelcomeComposer");
        _activeComposer = this.FindControl<StrataChatComposer>("ActiveComposer");
        _welcomePendingAttachmentList = this.FindControl<StrataAttachmentList>("WelcomePendingAttachmentList");
        _pendingAttachmentList = this.FindControl<StrataAttachmentList>("PendingAttachmentList");
        _welcomePanel = this.FindControl<Panel>("WelcomePanel");
        _chatPanel = this.FindControl<Panel>("ChatPanel");
        _dropOverlay = this.FindControl<Panel>("DropOverlay");
        _loadingOverlay = this.FindControl<Panel>("LoadingOverlay");

        // Hook scroll-near-top for incremental loading of older messages
        if (_chatShell is not null)
        {
            _chatShell.TemplateApplied += (_, te) =>
            {
                _transcriptScrollViewer = te.NameScope.Find<ScrollViewer>("PART_TranscriptScroll");
                if (_transcriptScrollViewer is not null)
                    _transcriptScrollViewer.ScrollChanged += OnTranscriptScrollChanged;
            };
        }

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is not ChatViewModel vm) return;

        // Guard against duplicate subscriptions from repeated OnDataContextChanged calls
        if (vm == _subscribedVm)
        {
            EnsureSettingsSubscription();
            return;
        }
        _subscribedVm = vm;
        EnsureSettingsSubscription();
        ApplyRuntimeSettings(vm);

        vm.ScrollToEndRequested += () => _chatShell?.ScrollToEnd();
        vm.UserMessageSent += () =>
        {
            _chatShell?.ResetAutoScroll();
            _chatShell?.ScrollToEnd();
        };

        // Populate available agents & skills for autocomplete
        PopulateComposerCatalogs(vm);

        // Update agent display on composers
        UpdateComposerAgent(vm.ActiveAgent);
        vm.AgentChanged += () => UpdateComposerAgent(vm.ActiveAgent);

        // Wire browser toggle button
        var browserToggle = this.FindControl<Button>("BrowserToggleButton");
        if (browserToggle is not null)
            browserToggle.Click += (_, _) => vm.ToggleBrowser();

        if (_welcomeComposer is not null)
        {
            _welcomeComposer.SendRequested += (_, _) =>
            {
                SyncModelFromComposer(_welcomeComposer, vm);
                vm.SendMessageCommand.Execute(null);
            };
            _welcomeComposer.StopRequested += (_, _) => vm.StopGenerationCommand.Execute(null);
            _welcomeComposer.AttachRequested += (_, _) => _ = PickAndAttachFilesAsync(vm);
            _welcomeComposer.AgentRemoved += (_, _) => vm.SetActiveAgent(null);
            _welcomeComposer.SkillRemoved += (_, args) =>
            {
                if (args is ComposerChipRemovedEventArgs chipArgs)
                {
                    var name = chipArgs.Item is StrataComposerChip sc ? sc.Name : chipArgs.Item?.ToString() ?? "";
                    vm.RemoveSkillByName(name);
                }
            };
            // Watch for agent selection via autocomplete
            _welcomeComposer.PropertyChanged += (_, args) =>
            {
                if (args.Property.Name == "AgentName")
                    OnComposerAgentChanged(_welcomeComposer, vm);
            };
            _welcomeComposer.VoiceRequested += (_, _) => _ = ToggleVoiceAsync(_welcomeComposer, vm);
        }

        if (_activeComposer is not null)
        {
            _activeComposer.SendRequested += (_, _) =>
            {
                SyncModelFromComposer(_activeComposer, vm);
                vm.SendMessageCommand.Execute(null);
            };
            _activeComposer.StopRequested += (_, _) => vm.StopGenerationCommand.Execute(null);
            _activeComposer.AttachRequested += (_, _) => _ = PickAndAttachFilesAsync(vm);
            _activeComposer.AgentRemoved += (_, _) => vm.SetActiveAgent(null);
            _activeComposer.SkillRemoved += (_, args) =>
            {
                if (args is ComposerChipRemovedEventArgs chipArgs)
                {
                    var name = chipArgs.Item is StrataComposerChip sc ? sc.Name : chipArgs.Item?.ToString() ?? "";
                    vm.RemoveSkillByName(name);
                }
            };
            // Watch for agent selection via autocomplete
            _activeComposer.PropertyChanged += (_, args) =>
            {
                if (args.Property.Name == "AgentName")
                    OnComposerAgentChanged(_activeComposer, vm);
            };
            _activeComposer.VoiceRequested += (_, _) => _ = ToggleVoiceAsync(_activeComposer, vm);
        }

        // Wire pending attachments list to the observable collection
        vm.PendingAttachments.CollectionChanged += (_, _) => RebuildPendingAttachmentChips(vm);

        // When a skill chip is added via composer autocomplete, register the ID (chip already added by composer)
        vm.ActiveSkillChips.CollectionChanged += (_, args) =>
        {
            if (vm.IsLoadingChat) return;
            if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
            {
                foreach (var item in args.NewItems)
                {
                    if (item is StrataComposerChip chip)
                        vm.RegisterSkillIdByName(chip.Name);
                }
            }
        };

        // When an MCP chip is added/removed via the MCP button popup, sync names with the VM
        vm.ActiveMcpChips.CollectionChanged += (_, args) =>
        {
            if (vm.IsLoadingChat) return;
            if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
            {
                foreach (var item in args.NewItems)
                {
                    if (item is StrataComposerChip chip)
                        vm.RegisterMcpByName(chip.Name);
                }
            }
            else if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems is not null)
            {
                foreach (var item in args.OldItems)
                {
                    if (item is StrataComposerChip chip)
                    {
                        vm.ActiveMcpServerNames.Remove(chip.Name);
                        vm.SyncActiveMcpsToChat();
                    }
                }
            }
        };

        // Wire file-created-by-tool to show attachment chips in the transcript
        vm.FileCreatedByTool += filePath =>
        {
            if (_shownFilePaths.Add(filePath))
                _pendingToolFileChips.Add(filePath);
        };

        // Feed terminal output into terminal preview cards
        vm.TerminalOutputReceived += (toolCallId, output, replaceExistingOutput) =>
        {
            if (string.IsNullOrEmpty(output))
                return;

            StrataTerminalPreview? target = null;
            if (!string.IsNullOrWhiteSpace(toolCallId))
                _terminalPreviewsByToolCallId.TryGetValue(toolCallId, out target);

            target ??= _activeTerminalPreview;
            if (target is null)
                return;

            if (replaceExistingOutput || string.IsNullOrEmpty(target.Output))
            {
                target.Output = output;
                return;
            }

            if (output.StartsWith(target.Output, StringComparison.Ordinal))
                target.Output = output;
            else if (!target.Output.EndsWith(output, StringComparison.Ordinal))
                target.Output = target.Output + "\n" + output;
        };

        // Collect search results for automatic source citations
        vm.SearchResultsCollected += results =>
        {
            foreach (var r in results)
                _pendingSearchSources.Add(new SearchSource { Title = r.Title, Snippet = r.Snippet, Url = r.Url });
        };

        // Render question cards when the LLM asks a question
        vm.QuestionAsked += (questionId, question, options, allowFreeText) =>
        {
            AddQuestionCard(vm, questionId, question, options, allowFreeText);
        };

        // Show/hide typing indicator when agent is busy
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.IsBusy))
            {
                if (vm.IsBusy)
                    ShowTypingIndicator(vm.StatusText);
                else
                    HideTypingIndicator();
            }
            else if (args.PropertyName == nameof(ChatViewModel.StatusText) && vm.IsBusy)
            {
                UpdateTypingIndicatorLabel(vm.StatusText);
            }
        };

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatViewModel.CurrentChat))
            {
                var hasChat = vm.CurrentChat is not null;
                if (_welcomePanel is not null) _welcomePanel.IsVisible = !hasChat;
                if (_chatPanel is not null) _chatPanel.IsVisible = hasChat;

                // Always rebuild when loading a chat (messages are already populated)
                _ = RebuildMessageStackAsync(vm);

                // Update project badge
                UpdateProjectBadge(vm);

                // Update browser toggle visibility
                UpdateBrowserToggle(vm);

                if (hasChat)
                    _chatShell?.ResetAutoScroll();
            }
            else if (args.PropertyName is nameof(ChatViewModel.HasUsedBrowser) or nameof(ChatViewModel.IsBrowserOpen))
            {
                UpdateBrowserToggle(vm);
            }
            else if (args.PropertyName == nameof(ChatViewModel.SelectedModel))
            {
                UpdateQualityLevels(vm.SelectedModel);
            }
        };

        vm.Messages.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
            {
                // Skip Add events during LoadChat — RebuildMessageStack handles them
                if (vm.IsLoadingChat) return;

                foreach (ChatMessageViewModel msgVm in args.NewItems)
                    AddMessageControl(msgVm);
            }
            else if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                _messageStack?.Children.Clear();
                _currentToolGroup = null;
                _currentToolGroupStack = null;
                _currentToolGroupCount = 0;
                _currentTodoToolCall = null;
                _currentTodoProgress = null;
                _todoUpdateCount = 0;
                _currentIntentText = null;
                _activeTerminalPreview = null;
                _terminalPreviewsByToolCallId.Clear();
                _messageCounter = 0;
                _shownFilePaths.Clear();
                _pendingToolFileChips.Clear();
                _pendingSearchSources.Clear();
                _pendingFetchedSkills.Clear();
                _pendingFileEdits.Clear();
                _toolStartTimes.Clear();
            }
        };
    }

    private void EnsureSettingsSubscription()
    {
        var mainVm = FindMainViewModel();
        var nextSettingsVm = mainVm?.SettingsVM;

        if (ReferenceEquals(_settingsVm, nextSettingsVm))
            return;

        if (_settingsVm is not null)
            _settingsVm.PropertyChanged -= OnSettingsViewModelPropertyChanged;

        _settingsVm = nextSettingsVm;

        if (_settingsVm is not null)
            _settingsVm.PropertyChanged += OnSettingsViewModelPropertyChanged;
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_subscribedVm is null)
            return;

        if (e.PropertyName is nameof(SettingsViewModel.SendWithEnter)
            or nameof(SettingsViewModel.PreferredModel))
        {
            ApplyRuntimeSettings(_subscribedVm);
        }

        if (e.PropertyName is nameof(SettingsViewModel.ShowTimestamps)
            or nameof(SettingsViewModel.ShowToolCalls)
            or nameof(SettingsViewModel.ShowReasoning))
        {
            _ = RebuildMessageStackAsync(_subscribedVm);
        }
    }

    private void ApplyRuntimeSettings(ChatViewModel vm)
    {
        var settings = _settingsVm;
        if (settings is null)
            return;

        if (_welcomeComposer is not null)
            _welcomeComposer.SendWithEnter = settings.SendWithEnter;
        if (_activeComposer is not null)
            _activeComposer.SendWithEnter = settings.SendWithEnter;

        if (!string.IsNullOrWhiteSpace(settings.PreferredModel)
            && vm.SelectedModel != settings.PreferredModel)
        {
            vm.SelectedModel = settings.PreferredModel;
        }
    }

    private async Task RebuildMessageStackAsync(ChatViewModel vm)
    {
        // Cancel any in-progress rebuild (e.g. user switching chats rapidly)
        _rebuildCts?.Cancel();
        var cts = new CancellationTokenSource();
        _rebuildCts = cts;
        var token = cts.Token;

        _transcriptBuildDepth++;

        // Build controls into a DETACHED StackPanel (not in the visual tree).
        // This avoids expensive per-control layout/measure/scroll passes.
        // We only attach it to the shell after all controls are built.
        _messageStack = new StackPanel { Spacing = 12 };
        _currentToolGroup = null;
        _currentToolGroupStack = null;
        _currentToolGroupCount = 0;
        _currentTodoToolCall = null;
        _currentTodoProgress = null;
        _todoUpdateCount = 0;
        _currentIntentText = null;
        _activeTerminalPreview = null;
        _terminalPreviewsByToolCallId.Clear();
        _shownFilePaths.Clear();
        _pendingToolFileChips.Clear();
        _pendingSearchSources.Clear();
        _pendingFetchedSkills.Clear();
        _pendingFileEdits.Clear();
        _toolStartTimes.Clear();
        _deferredMessages = null;
        _isLoadingOlder = false;

        // Clear the transcript immediately so the old chat disappears
        if (_chatShell is not null)
            _chatShell.Transcript = null;

        var messages = vm.Messages.ToList();
        var count = messages.Count;

        // For large chats, only render the most recent messages initially.
        // Older messages are deferred and loaded when the user scrolls to top.
        const int initialRenderMax = 20;

        int startIndex = 0;
        if (count > initialRenderMax)
        {
            startIndex = count - initialRenderMax;
            // Snap to a user message (turn boundary)
            while (startIndex < count && messages[startIndex].Role != "user")
                startIndex++;
            if (startIndex >= count)
                startIndex = count - initialRenderMax;

            _deferredMessages = messages.GetRange(0, startIndex);
        }

        int renderCount = count - startIndex;

        // Show loading overlay for non-trivial chats and yield a full frame
        if (renderCount > 6 && _loadingOverlay is not null)
        {
            _loadingOverlay.IsVisible = true;
            await Task.Delay(16);
        }

        if (token.IsCancellationRequested)
        {
            if (_transcriptBuildDepth > 0)
                _transcriptBuildDepth--;
            return;
        }

        try
        {
            // Build all controls into the detached stack (no layout cost per-add)
            for (int i = startIndex; i < count; i++)
            {
                if (token.IsCancellationRequested) return;
                AddMessageControl(messages[i]);
            }

            if (token.IsCancellationRequested) return;

            CloseToolGroup();
            CollapseAllCompletedTurns();

            // NOW attach the fully-built stack to the visual tree (single layout pass)
            if (_chatShell is not null)
                _chatShell.Transcript = _messageStack;

            // Scroll to bottom after loading chat history
            if (vm.CurrentChat is not null)
                Dispatcher.UIThread.Post(() => _chatShell?.ScrollToEnd(), DispatcherPriority.Loaded);
        }
        finally
        {
            if (_transcriptBuildDepth > 0)
                _transcriptBuildDepth--;

            if (_loadingOverlay is not null)
                _loadingOverlay.IsVisible = false;
        }
    }

    /// <summary>
    /// Detects when the user scrolls near the top of the transcript and loads older
    /// deferred messages incrementally.
    /// </summary>
    private void OnTranscriptScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_transcriptScrollViewer is null || _deferredMessages is null || _deferredMessages.Count == 0)
            return;
        if (_isLoadingOlder)
            return;

        // Trigger when scroll offset is near the top
        if (_transcriptScrollViewer.Offset.Y < 100)
            _ = LoadOlderMessagesAsync();
    }

    /// <summary>
    /// Prepends a batch of older (deferred) messages to the top of the transcript,
    /// preserving the user's current scroll position.
    /// </summary>
    private async Task LoadOlderMessagesAsync()
    {
        if (_deferredMessages is null || _deferredMessages.Count == 0 || _messageStack is null)
            return;

        _isLoadingOlder = true;
        _transcriptBuildDepth++;

        try
        {
            // Take the last N deferred messages (most recent of the older set)
            const int batchSize = 15;
            int takeFrom = Math.Max(0, _deferredMessages.Count - batchSize);

            // Find turn boundary: snap to a user message
            while (takeFrom > 0 && _deferredMessages[takeFrom].Role != "user")
                takeFrom--;

            var batch = _deferredMessages.GetRange(takeFrom, _deferredMessages.Count - takeFrom);
            _deferredMessages = takeFrom > 0 ? _deferredMessages.GetRange(0, takeFrom) : null;

            // Record current scroll extent so we can preserve position after prepending
            var scrollBefore = _transcriptScrollViewer?.Extent.Height ?? 0;

            // Build controls for the batch into a temporary stack, then prepend
            var tempStack = new StackPanel { Spacing = 12 };
            var savedMessageStack = _messageStack;
            var savedToolGroup = _currentToolGroup;
            var savedToolGroupStack = _currentToolGroupStack;
            var savedToolGroupCount = _currentToolGroupCount;
            var savedTodoToolCall = _currentTodoToolCall;
            var savedTodoProgress = _currentTodoProgress;
            var savedTodoUpdateCount = _todoUpdateCount;
            var savedIntentText = _currentIntentText;
            var savedTerminalPreview = _activeTerminalPreview;
            var savedTerminalPreviewMap = new Dictionary<string, StrataTerminalPreview>(_terminalPreviewsByToolCallId, StringComparer.Ordinal);

            _messageStack = tempStack;
            _currentToolGroup = null;
            _currentToolGroupStack = null;
            _currentToolGroupCount = 0;
            _currentTodoToolCall = null;
            _currentTodoProgress = null;
            _todoUpdateCount = 0;
            _currentIntentText = null;
            _activeTerminalPreview = null;
            _terminalPreviewsByToolCallId.Clear();

            foreach (var msgVm in batch)
                AddMessageControl(msgVm);

            CloseToolGroup();

            // Collapse completed turns in the prepended batch
            CollapseAllCompletedTurns();

            // Restore state
            _messageStack = savedMessageStack;
            _currentToolGroup = savedToolGroup;
            _currentToolGroupStack = savedToolGroupStack;
            _currentToolGroupCount = savedToolGroupCount;
            _currentTodoToolCall = savedTodoToolCall;
            _currentTodoProgress = savedTodoProgress;
            _todoUpdateCount = savedTodoUpdateCount;
            _currentIntentText = savedIntentText;
            _activeTerminalPreview = savedTerminalPreview;
            _terminalPreviewsByToolCallId.Clear();
            foreach (var kv in savedTerminalPreviewMap)
                _terminalPreviewsByToolCallId[kv.Key] = kv.Value;

            // Prepend the batch controls to the real message stack
            var children = tempStack.Children.ToList();
            for (int i = children.Count - 1; i >= 0; i--)
            {
                tempStack.Children.RemoveAt(i);
                _messageStack.Children.Insert(0, children[i]);
            }

            // Yield to let layout update
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            // Restore scroll position: new content was prepended, so offset by the height difference
            if (_transcriptScrollViewer is not null)
            {
                var scrollAfter = _transcriptScrollViewer.Extent.Height;
                var heightAdded = scrollAfter - scrollBefore;
                if (heightAdded > 0)
                    _transcriptScrollViewer.Offset = new Vector(_transcriptScrollViewer.Offset.X,
                        _transcriptScrollViewer.Offset.Y + heightAdded);
            }
        }
        finally
        {
            if (_transcriptBuildDepth > 0)
                _transcriptBuildDepth--;

            _isLoadingOlder = false;
        }
    }

    private void AddMessageControl(ChatMessageViewModel msgVm)
    {
        if (_messageStack is null)
        {
            _messageStack = new StackPanel { Spacing = 12 };
            if (_chatShell is not null)
                _chatShell.Transcript = _messageStack;
        }

        var role = msgVm.Role switch
        {
            "user" => StrataChatRole.User,
            "assistant" => StrataChatRole.Assistant,
            "tool" => StrataChatRole.Tool,
            "reasoning" => StrataChatRole.System,
            "system" => StrataChatRole.System,
            _ => StrataChatRole.Assistant
        };

        var showToolCalls = _settingsVm?.ShowToolCalls ?? true;
        var showReasoning = _settingsVm?.ShowReasoning ?? true;
        var showTimestamps = _settingsVm?.ShowTimestamps ?? true;

        if (msgVm.Role == "tool")
        {
            var toolName = msgVm.ToolName ?? "";
            var initialStatus = msgVm.ToolStatus switch
            {
                "Completed" => StrataAiToolCallStatus.Completed,
                "Failed" => StrataAiToolCallStatus.Failed,
                _ => StrataAiToolCallStatus.InProgress
            };

            // stop_powershell/write_powershell: skip in UI
            if (toolName is "stop_powershell" or "write_powershell")
                return;

            // read_powershell: skip UI card but handled via TerminalOutputReceived event
            if (toolName is "read_powershell")
                return;

            // ask_question: during replay, render static pre-answered card; during live, rendered via QuestionAsked event
            if (toolName is "ask_question")
            {
                if (IsTranscriptBuilding)
                {
                    var question = ExtractJsonField(msgVm.Content, "question") ?? "";
                    var opts = ExtractJsonField(msgVm.Content, "options") ?? "";
                    var answer = msgVm.Message.ToolOutput;
                    if (!string.IsNullOrEmpty(answer) && answer.StartsWith("User answered: "))
                        answer = answer["User answered: ".Length..];

                    CloseToolGroup();
                    var card = new StrataQuestionCard
                    {
                        Question = question,
                        Options = opts,
                        AllowFreeText = false,
                        Margin = new Thickness(0, 4, 0, 4),
                        Name = $"QuestionCard_{_messageCounter++}",
                    };
                    if (!string.IsNullOrEmpty(answer))
                    {
                        card.SelectedAnswer = answer;
                        card.IsAnswered = true;
                    }

                    _messageStack?.Children.Add(card);
                }
                return;
            }

            // announce_file: don't show a tool card — collect the file for attachment chip
            if (toolName == "announce_file")
            {
                var filePath = ExtractJsonField(msgVm.Content, "filePath");
                if (filePath is not null && File.Exists(filePath) && _shownFilePaths.Add(filePath))
                    _pendingToolFileChips.Add(filePath);
                return;
            }

            // fetch_skill: collect the skill reference for display on the assistant message
            if (toolName == "fetch_skill")
            {
                var skillName = ExtractJsonField(msgVm.Content, "name");
                if (!string.IsNullOrEmpty(skillName))
                {
                    var glyph = "\u26A1";
                    if (DataContext is ChatViewModel chatVm)
                    {
                        var skill = chatVm.FindSkillByName(skillName);
                        if (skill is not null) glyph = skill.IconGlyph;
                    }
                    _pendingFetchedSkills.Add(new SkillReference { Name = skillName, Glyph = glyph });
                }
                return;
            }

            // report_intent: don't show a tool card — capture intent text for group label
            if (toolName == "report_intent")
            {
                var intentText = ExtractJsonField(msgVm.Content, "intent");
                if (!string.IsNullOrEmpty(intentText))
                {
                    _currentIntentText = intentText;

                    // Only create UI group if tool calls are visible
                    if (showToolCalls)
                    {
                        EnsureCurrentToolGroup(initialStatus);
                        UpdateToolGroupLabel();
                    }
                }
                return;
            }

            if (toolName is "update_todo" or "manage_todo_list")
            {
                if (!showToolCalls)
                    return;

                var steps = ParseTodoSteps(msgVm.Content);
                if (steps.Count == 0)
                    return;

                EnsureCurrentToolGroup(initialStatus);
                _todoUpdateCount++;
                UpsertTodoProgressToolCall(steps, msgVm.ToolStatus ?? "InProgress");
                if (_currentToolGroup is not null
                    && initialStatus == StrataAiToolCallStatus.InProgress
                    && !IsTranscriptBuilding)
                {
                    _currentToolGroup.IsExpanded = true;
                }
                UpdateToolGroupLabel();

                msgVm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ChatMessageViewModel.ToolStatus)
                        && _currentTodoProgress is not null)
                    {
                        _currentTodoProgress.ToolStatus = msgVm.ToolStatus ?? "InProgress";
                        if (_currentTodoToolCall is not null)
                        {
                            _currentTodoToolCall.Status = msgVm.ToolStatus switch
                            {
                                "Completed" => StrataAiToolCallStatus.Completed,
                                "Failed" => StrataAiToolCallStatus.Failed,
                                _ => StrataAiToolCallStatus.InProgress
                            };
                        }
                        UpdateToolGroupLabel();
                    }
                };
                return;
            }

            if (!showToolCalls)
                return;

            var (friendlyName, friendlyInfo) = GetFriendlyToolDisplay(toolName, msgVm.Author, msgVm.Content);
            friendlyName = $"{GetToolGlyph(toolName)} {friendlyName}";

            // Track start time for duration calculation (live sessions only)
            var toolCallId = msgVm.Message.ToolCallId;
            if (toolCallId is not null && initialStatus == StrataAiToolCallStatus.InProgress)
                _toolStartTimes[toolCallId] = Stopwatch.GetTimestamp();

            // Powershell tool: show a terminal preview instead of a plain tool card
            if (toolName == "powershell")
            {
                var command = ExtractJsonField(msgVm.Content, "command") ?? "";
                var termPreview = new StrataTerminalPreview
                {
                    ToolName = friendlyName,
                    Command = command,
                    Output = msgVm.Message.ToolOutput ?? string.Empty,
                    Status = initialStatus,
                    IsExpanded = !IsTranscriptBuilding,
                };
                _activeTerminalPreview = termPreview;
                if (toolCallId is not null)
                    _terminalPreviewsByToolCallId[toolCallId] = termPreview;

                msgVm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ChatMessageViewModel.ToolStatus))
                    {
                        termPreview.Status = msgVm.ToolStatus switch
                        {
                            "Completed" => StrataAiToolCallStatus.Completed,
                            "Failed" => StrataAiToolCallStatus.Failed,
                            _ => StrataAiToolCallStatus.InProgress
                        };

                        if (toolCallId is not null && termPreview.Status is not StrataAiToolCallStatus.InProgress
                            && _toolStartTimes.TryGetValue(toolCallId, out var startTick))
                        {
                            var elapsed = Stopwatch.GetElapsedTime(startTick);
                            termPreview.DurationMs = elapsed.TotalMilliseconds;
                            _toolStartTimes.Remove(toolCallId);
                        }

                        UpdateToolGroupLabel();
                    }
                };

                EnsureCurrentToolGroup(initialStatus);
                _currentToolGroupStack!.Children.Add(termPreview);
                _currentToolGroupCount++;

                if (_currentToolGroup is not null
                    && initialStatus == StrataAiToolCallStatus.InProgress
                    && !IsTranscriptBuilding)
                {
                    _currentToolGroup.IsExpanded = true;
                }

                UpdateToolGroupLabel();
                return;
            }

            var toolCall = new StrataAiToolCall
            {
                ToolName = friendlyName,
                Status = initialStatus,
                IsExpanded = false,
                InputParameters = FormatToolArgsFriendly(toolName, msgVm.Content),
                MoreInfo = friendlyInfo
            };

            // For file-edit tools, collect diff data and set a "Show diff" action on the header
            if (IsFileEditTool(toolName))
            {
                var argsJson = msgVm.Content;

                // Collect all file edits for the file changes section
                var allDiffs = ExtractAllDiffs(toolName, argsJson);
                foreach (var d in allDiffs)
                    _pendingFileEdits.Add((d.FilePath, toolName, d.OldText, d.NewText));

                // Show diff button in the tool call header
                if (allDiffs.Count > 0)
                {
                    var capturedDiff = allDiffs[0];
                    var showDiffBtn = new Button
                    {
                        Content = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 4,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = "\uE8A7", // diff icon
                                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                                    FontSize = 10,
                                    VerticalAlignment = VerticalAlignment.Center,
                                },
                                new TextBlock { Text = Loc.ShowDiff, FontSize = 10, VerticalAlignment = VerticalAlignment.Center }
                            }
                        },
                        Classes = { "subtle" },
                        Padding = new Thickness(6, 1),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    };
                    showDiffBtn.Click += (_, e) =>
                    {
                        e.Handled = true; // prevent header toggle
                        if (DataContext is ChatViewModel chatVm)
                            chatVm.ShowDiff(capturedDiff.FilePath, capturedDiff.OldText, capturedDiff.NewText);
                    };
                    toolCall.HeaderAction = showDiffBtn;
                }
            }

            msgVm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChatMessageViewModel.ToolStatus))
                {
                    toolCall.Status = msgVm.ToolStatus switch
                    {
                        "Completed" => StrataAiToolCallStatus.Completed,
                        "Failed" => StrataAiToolCallStatus.Failed,
                        _ => StrataAiToolCallStatus.InProgress
                    };

                    // Calculate duration from tracked start time
                    if (toolCallId is not null && toolCall.Status is not StrataAiToolCallStatus.InProgress
                        && _toolStartTimes.TryGetValue(toolCallId, out var startTick))
                    {
                        var elapsed = Stopwatch.GetElapsedTime(startTick);
                        toolCall.DurationMs = elapsed.TotalMilliseconds;
                        _toolStartTimes.Remove(toolCallId);
                    }

                    UpdateToolGroupLabel();
                }
            };

            // Group consecutive tool calls inside a StrataThink
            EnsureCurrentToolGroup(initialStatus);

            _currentToolGroupStack!.Children.Add(toolCall);
            _currentToolGroupCount++;

            UpdateToolGroupLabel();
        }
        else if (msgVm.Role == "reasoning")
        {
            CloseToolGroup();

            if (!showReasoning)
                return;

            var think = new StrataThink
            {
                Label = Loc.Tool_ReasoningLabel,
                IsExpanded = false
            };

            var md = new StrataMarkdown { Markdown = msgVm.Content, IsInline = true };
            think.Content = md;

            msgVm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChatMessageViewModel.Content))
                    md.Markdown = msgVm.Content;
            };

            InsertBeforeTypingIndicator(think);
        }
        else
        {
            CloseToolGroup();

            var md = new StrataMarkdown { Markdown = msgVm.Content, IsInline = true };

            var isUser = role == StrataChatRole.User;

            // For user messages with attachments or skills, wrap content + extras in a StackPanel
            var hasAttachments = isUser && msgVm.Message.Attachments.Count > 0;
            var hasSkills = isUser && msgVm.Message.ActiveSkills.Count > 0;
            object msgContent;
            if (hasAttachments || hasSkills)
            {
                var contentStack = new StackPanel { Spacing = 6 };
                contentStack.Children.Add(md);

                if (hasAttachments)
                {
                    var attachList = new StrataAttachmentList { ShowAddButton = false };
                    foreach (var filePath in msgVm.Message.Attachments)
                    {
                        var chip = CreateFileChip(filePath, isRemovable: false);
                        attachList.Items.Add(chip);
                    }
                    contentStack.Children.Add(attachList);
                }

                if (hasSkills)
                    contentStack.Children.Add(BuildSkillChips(msgVm.Message.ActiveSkills));

                msgContent = contentStack;
            }
            else if (!isUser && (_pendingToolFileChips.Count > 0 || _pendingSearchSources.Count > 0 || _pendingFetchedSkills.Count > 0 || _pendingFileEdits.Count > 0 || msgVm.Message.Sources.Count > 0 || msgVm.Message.ActiveSkills.Count > 0))
            {
                // Attach files, search sources, and fetched skills to the assistant message
                var contentStack = new StackPanel { Spacing = 6 };
                contentStack.Children.Add(md);

                // Collect fetched skills: live pending + persisted on message
                var allSkills = new List<SkillReference>();
                if (_pendingFetchedSkills.Count > 0)
                {
                    allSkills.AddRange(_pendingFetchedSkills);
                    _pendingFetchedSkills.Clear();
                }
                if (msgVm.Message.ActiveSkills.Count > 0)
                    allSkills.AddRange(msgVm.Message.ActiveSkills);

                if (allSkills.Count > 0)
                    contentStack.Children.Add(BuildSkillChips(allSkills));

                if (_pendingToolFileChips.Count > 0)
                {
                    var attachList = new StrataAttachmentList { ShowAddButton = false };
                    foreach (var filePath in _pendingToolFileChips)
                    {
                        var chip = CreateFileChip(filePath, isRemovable: false);
                        attachList.Items.Add(chip);
                    }
                    contentStack.Children.Add(attachList);
                    _pendingToolFileChips.Clear();
                }

                // Collect sources: live pending + persisted on message
                var allSources = new List<SearchSource>();
                if (_pendingSearchSources.Count > 0)
                {
                    allSources.AddRange(_pendingSearchSources);
                    _pendingSearchSources.Clear();
                }
                if (msgVm.Message.Sources.Count > 0)
                    allSources.AddRange(msgVm.Message.Sources);

                if (allSources.Count > 0)
                    contentStack.Children.Add(BuildSourcesSection(allSources));

                if (_pendingFileEdits.Count > 0)
                {
                    contentStack.Children.Add(BuildFileChangesSection(_pendingFileEdits));
                    _pendingFileEdits.Clear();
                }

                msgContent = contentStack;
            }
            else
            {
                msgContent = md;
            }

            var msg = new StrataChatMessage
            {
                Name = $"msg_{_messageCounter++}",
                Role = role,
                Author = msgVm.Author ?? "",
                Timestamp = showTimestamps ? msgVm.TimestampText : string.Empty,
                IsStreaming = msgVm.IsStreaming,
                IsEditable = isUser,
                Content = msgContent
            };

            // For user messages: wire edit to extract markdown, and retry to resend
            if (isUser)
            {
                msg.EditRequested += (_, _) =>
                {
                    msg.EditText = msgVm.Content;
                };

                msg.EditConfirmed += (_, _) =>
                {
                    if (msg.EditText is not null)
                    {
                        msgVm.Message.Content = msg.EditText;
                        msgVm.NotifyContentChanged();

                        // Resend from this message
                        if (DataContext is ChatViewModel chatVm)
                            _ = chatVm.ResendFromMessageAsync(msgVm.Message);
                    }
                };

                msg.RegenerateRequested += (_, _) =>
                {
                    // Retry: resend the same user message
                    if (DataContext is ChatViewModel chatVm)
                        _ = chatVm.ResendFromMessageAsync(msgVm.Message);
                };
            }

            msgVm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChatMessageViewModel.Content))
                    md.Markdown = msgVm.Content;
                if (args.PropertyName == nameof(ChatMessageViewModel.IsStreaming))
                {
                    msg.IsStreaming = msgVm.IsStreaming;
                    // When assistant streaming ends, check final content for file references
                    // (initial check during streaming only sees partial content)
                    if (!msgVm.IsStreaming && role == StrataChatRole.Assistant)
                    {
                        AddFileReferencesFromContent(msgVm.Content);
                        CollapseCompletedTurnBlocks(msg);
                    }
                }
            };

            InsertBeforeTypingIndicator(msg);

            // For assistant messages: detect file paths in content and show reference chips
            if (!isUser && !string.IsNullOrEmpty(msgVm.Content))
            {
                AddFileReferencesFromContent(msgVm.Content);
            }
        }

        _chatShell?.ScrollToEnd();
    }

    private void EnsureCurrentToolGroup(StrataAiToolCallStatus initialStatus)
    {
        if (_currentToolGroup is not null)
            return;

        _currentToolGroupStack = new StackPanel { Spacing = 4 };
        _currentToolGroupCount = 0;
        _currentTodoToolCall = null;
        _currentTodoProgress = null;

        _currentToolGroup = new StrataThink
        {
            Label = _currentIntentText is not null
                ? _currentIntentText + "\u2026"
                : Loc.ToolGroup_Working,
            IsExpanded = false,
            IsActive = initialStatus == StrataAiToolCallStatus.InProgress,
            Meta = null,
            ProgressValue = -1,
            Content = _currentToolGroupStack
        };

        InsertBeforeTypingIndicator(_currentToolGroup);
    }

    private void CloseToolGroup()
    {
        if (_currentToolGroup is not null)
        {
            // If the group has no tool cards at all, remove it entirely
            if (_currentToolGroupCount == 0)
            {
                _messageStack?.Children.Remove(_currentToolGroup);
            }
            else
            {
                UpdateToolGroupLabel();
            }

            _currentToolGroup = null;
            _currentToolGroupStack = null;
            _currentToolGroupCount = 0;
            _currentTodoToolCall = null;
            _currentTodoProgress = null;
            _todoUpdateCount = 0;
            _currentIntentText = null;
            _activeTerminalPreview = null;
            _terminalPreviewsByToolCallId.Clear();
        }
    }

    /// <summary>
    /// After an assistant message finishes, merges all consecutive StrataThink blocks
    /// (tool groups and reasoning) preceding it into a single collapsible group.
    /// </summary>
    private void CollapseCompletedTurnBlocks(Control assistantMsgControl)
    {
        if (_messageStack is null) return;

        var idx = _messageStack.Children.IndexOf(assistantMsgControl);
        if (idx <= 0) return;

        // Walk backward collecting consecutive StrataThink controls
        // Skip browser cards so they remain visible after the turn is collapsed
        var blocksToMerge = new List<StrataThink>();
        for (int i = idx - 1; i >= 0; i--)
        {
            if (_messageStack.Children[i] is StrataThink think)
                blocksToMerge.Add(think);
            else
                break;
        }

        // A single StrataThink is already a collapsible unit.
        // Wrapping it again creates a nested "click to expand" UX.
        if (blocksToMerge.Count < 2) return;

        blocksToMerge.Reverse();

        // Count items for summary label
        int totalToolCalls = 0;
        int failedCount = 0;
        bool hasReasoning = false;
        bool hasTodoProgress = false;
        string? todoMeta = null;

        foreach (var think in blocksToMerge)
        {
            if (think.Content is StackPanel toolStack)
            {
                foreach (var child in toolStack.Children)
                {
                    if (child is StrataAiToolCall tc)
                    {
                        totalToolCalls++;
                        if (tc.Status == StrataAiToolCallStatus.Failed) failedCount++;

                        if (!string.IsNullOrWhiteSpace(tc.ToolName)
                            && tc.ToolName.Contains(Loc.ToolTodo_Title, StringComparison.CurrentCultureIgnoreCase))
                        {
                            hasTodoProgress = true;
                            if (!string.IsNullOrWhiteSpace(think.Meta))
                                todoMeta = think.Meta;
                            else if (!string.IsNullOrWhiteSpace(tc.MoreInfo))
                                todoMeta = tc.MoreInfo;
                        }
                    }
                }
            }
            else
            {
                hasReasoning = true;
            }
        }

        // Build summary label
        string label;
        if (hasTodoProgress)
        {
            label = !string.IsNullOrWhiteSpace(todoMeta)
                ? $"{Loc.ToolTodo_Title} · {todoMeta}"
                : Loc.ToolTodo_Title;
        }
        else if (hasReasoning && totalToolCalls > 0)
            label = totalToolCalls == 1
                ? Loc.TurnSummary_ReasonedAndOneAction
                : string.Format(Loc.TurnSummary_ReasonedAndActions, totalToolCalls);
        else if (totalToolCalls > 0)
            label = totalToolCalls == 1
                ? Loc.ToolGroup_FinishedOne
                : string.Format(Loc.ToolGroup_FinishedCount, totalToolCalls);
        else
            label = Loc.TurnSummary_ReasonedAndOneAction;

        if (failedCount > 0)
            label += " " + string.Format(Loc.ToolGroup_FinishedFailed, failedCount);

        // Remove original blocks from message stack
        int firstIdx = _messageStack.Children.IndexOf(blocksToMerge[0]);
        foreach (var think in blocksToMerge)
            _messageStack.Children.Remove(think);

        // Re-insert the original blocks inside a StrataTurnSummary
        var innerStack = new StackPanel { Spacing = 8 };
        foreach (var think in blocksToMerge)
            innerStack.Children.Add(think);

        var summary = new StrataTurnSummary
        {
            Label = label,
            IsExpanded = hasTodoProgress && !IsTranscriptBuilding,
            HasFailures = failedCount > 0,
            Content = innerStack
        };

        _messageStack.Children.Insert(firstIdx, summary);
    }

    /// <summary>
    /// Post-processes the entire message stack to collapse all completed assistant turns.
    /// Called at the end of RebuildMessageStack when loading from history.
    /// </summary>
    private void CollapseAllCompletedTurns()
    {
        if (_messageStack is null) return;

        var assistantMessages = _messageStack.Children
            .OfType<StrataChatMessage>()
            .Where(m => m.Role == StrataChatRole.Assistant)
            .ToList();

        // Process last-to-first so index shifts don't affect earlier messages
        for (int i = assistantMessages.Count - 1; i >= 0; i--)
            CollapseCompletedTurnBlocks(assistantMessages[i]);
    }

    private void UpdateToolGroupLabel()
    {
        if (_currentToolGroup is null) return;

        var isHistoricalRender = IsTranscriptBuilding;

        if (_currentTodoProgress is not null && _currentTodoProgress.Total > 0)
        {
            var todoDone = _currentTodoProgress.Completed + _currentTodoProgress.Failed;
            var running = Math.Max(0, _currentTodoProgress.Total - todoDone);

            _currentToolGroup.Label = Loc.ToolTodo_Title;
            _currentToolGroup.Meta = _currentTodoProgress.Failed > 0
                ? string.Format(Loc.ToolTodo_MetaWithFailed, _currentTodoProgress.Completed, _currentTodoProgress.Total, _currentTodoProgress.Failed)
                : string.Format(Loc.ToolTodo_Meta, _currentTodoProgress.Completed, _currentTodoProgress.Total);

            if (_todoUpdateCount > 1)
                _currentToolGroup.Meta += " · " + string.Format(Loc.ToolTodo_Updates, _todoUpdateCount);

            var progress = Math.Clamp((todoDone * 100d) / _currentTodoProgress.Total, 0d, 100d);
            _currentToolGroup.ProgressValue = isHistoricalRender ? -1 : progress;
            _currentToolGroup.IsActive = running > 0 && _currentTodoProgress.ToolStatus != "Failed";

            if (!_currentToolGroup.IsActive || isHistoricalRender)
                _currentToolGroup.IsExpanded = false;

            return;
        }

        var completedCount = 0;
        var failedCount = 0;
        if (_currentToolGroupStack is not null)
        {
            foreach (var child in _currentToolGroupStack.Children)
            {
                if (child is StrataAiToolCall tc)
                {
                    if (tc.Status == StrataAiToolCallStatus.Completed) completedCount++;
                    else if (tc.Status == StrataAiToolCallStatus.Failed) failedCount++;
                }
                else if (child is StrataTerminalPreview tp)
                {
                    if (tp.Status == StrataAiToolCallStatus.Completed) completedCount++;
                    else if (tp.Status == StrataAiToolCallStatus.Failed) failedCount++;
                }
            }
        }

        if (_currentToolGroupCount <= 0)
        {
            _currentToolGroup.Meta = null;
            _currentToolGroup.ProgressValue = -1;
            _currentToolGroup.IsActive = true;
            _currentToolGroup.Label = _currentIntentText is not null
                ? _currentIntentText + "\u2026"
                : Loc.ToolGroup_Working;
            return;
        }

        var runningCount = Math.Max(0, _currentToolGroupCount - completedCount - failedCount);

        var allDone = completedCount + failedCount == _currentToolGroupCount && _currentToolGroupCount > 0;
        if (allDone)
        {
            if (_currentIntentText is not null)
            {
                // Use the intent text as the completed label
                _currentToolGroup.Label = failedCount > 0
                    ? string.Format(Loc.ToolGroup_FinishedWithFailed, _currentIntentText, failedCount)
                    : _currentIntentText;
            }
            else
            {
                _currentToolGroup.Label = failedCount > 0
                    ? string.Format(Loc.ToolGroup_FinishedFailed, failedCount)
                    : _currentToolGroupCount == 1 ? Loc.ToolGroup_Finished : string.Format(Loc.ToolGroup_FinishedCount, _currentToolGroupCount);
            }
            _currentToolGroup.IsActive = false;

            _currentToolGroup.Meta = failedCount > 0
                ? string.Format(Loc.ToolGroup_MetaFailed, completedCount, _currentToolGroupCount, failedCount)
                : string.Format(Loc.ToolGroup_MetaDone, completedCount, _currentToolGroupCount);

            if (isHistoricalRender)
                _currentToolGroup.IsExpanded = false;
        }
        else
        {
            _currentToolGroup.IsActive = true;
            if (_currentIntentText is not null)
            {
                _currentToolGroup.Label = _currentIntentText + "\u2026";
            }
            else
            {
                _currentToolGroup.Label = _currentToolGroupCount == 1
                    ? Loc.ToolGroup_Working
                    : string.Format(Loc.ToolGroup_WorkingCount, _currentToolGroupCount);
            }

            _currentToolGroup.Meta = runningCount > 0
                ? string.Format(Loc.ToolGroup_MetaRunning, completedCount, _currentToolGroupCount, runningCount)
                : string.Format(Loc.ToolGroup_MetaDone, completedCount, _currentToolGroupCount);

            if (isHistoricalRender)
                _currentToolGroup.IsExpanded = false;
        }

        var genericProgress = _currentToolGroupCount > 0
            ? Math.Clamp(((completedCount + failedCount) * 100d) / _currentToolGroupCount, 0d, 100d)
            : -1;
        _currentToolGroup.ProgressValue = isHistoricalRender ? -1 : genericProgress;
    }

    private static void SyncModelFromComposer(StrataChatComposer composer, ChatViewModel vm)
    {
        var selected = composer.SelectedModel?.ToString();
        if (!string.IsNullOrEmpty(selected) && selected != vm.SelectedModel)
            vm.SelectedModel = selected;
    }

    private void AddQuestionCard(ChatViewModel vm, string questionId, string question, string options, bool allowFreeText)
    {
        if (_messageStack is null) return;

        // Close any pending tool group so the question card appears standalone
        CloseToolGroup();

        var card = new StrataQuestionCard
        {
            Question = question,
            Options = options,
            AllowFreeText = allowFreeText,
            Margin = new Thickness(0, 4, 0, 4),
            Name = $"QuestionCard_{_messageCounter++}",
        };

        card.AnswerSubmitted += (_, answer) =>
        {
            vm.SubmitQuestionAnswer(questionId, answer);
        };

        _messageStack.Children.Add(card);
        _chatShell?.ScrollToEnd();
    }

    private void ShowTypingIndicator(string? label)
    {
        if (_messageStack is null) return;

        if (_typingIndicator is null)
        {
            _typingIndicator = new StrataTypingIndicator
            {
                Label = label ?? Loc.Status_Thinking,
                IsActive = true
            };
            _messageStack.Children.Add(_typingIndicator);
        }
        else
        {
            _typingIndicator.Label = label ?? Loc.Status_Thinking;
            _typingIndicator.IsActive = true;
            // Ensure it's at the bottom
            if (_messageStack.Children.Contains(_typingIndicator))
                _messageStack.Children.Remove(_typingIndicator);
            _messageStack.Children.Add(_typingIndicator);
        }

        _chatShell?.ScrollToEnd();
    }

    private void HideTypingIndicator()
    {
        if (_typingIndicator is not null && _messageStack is not null)
        {
            _messageStack.Children.Remove(_typingIndicator);
            _typingIndicator = null;
        }
    }

    private void UpdateTypingIndicatorLabel(string? label)
    {
        if (_typingIndicator is not null && !string.IsNullOrEmpty(label))
            _typingIndicator.Label = label;
    }

    private void InsertBeforeTypingIndicator(Control control)
    {
        if (_messageStack is null) return;

        if (_typingIndicator is not null && _messageStack.Children.Contains(_typingIndicator))
        {
            var idx = _messageStack.Children.IndexOf(_typingIndicator);
            _messageStack.Children.Insert(idx, control);
        }
        else
        {
            _messageStack.Children.Add(control);
        }
    }

    private void AddFileReferencesFromContent(string content)
    {
        var paths = ChatViewModel.ExtractFilePathsFromContent(content);
        if (paths.Length == 0) return;

        var attachList = new StrataAttachmentList { ShowAddButton = false };
        var anyAdded = false;

        foreach (var fp in paths)
        {
            if (File.Exists(fp) && _shownFilePaths.Add(fp))
            {
                var chip = CreateFileChip(fp, isRemovable: false);
                attachList.Items.Add(chip);
                anyAdded = true;
            }
        }

        if (anyAdded)
            InsertBeforeTypingIndicator(attachList);
    }

    private static string[] ReasoningLevels => [Loc.Quality_Low, Loc.Quality_Medium, Loc.Quality_High];

    private static bool IsReasoningModel(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return false;
        // Models that support reasoning effort: o-series, and models with "thinking" capability
        return modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("think", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateQualityLevels(string? modelId)
    {
        var levels = IsReasoningModel(modelId) ? ReasoningLevels : null;
        if (_welcomeComposer is not null) _welcomeComposer.QualityLevels = levels;
        if (_activeComposer is not null) _activeComposer.QualityLevels = levels;
    }

    /// <summary>
    /// Populates the AvailableAgents and AvailableSkills catalogs on both composers
    /// so the user can type @ or / to trigger autocomplete.
    /// </summary>
    /// <summary>Focuses the active chat composer input, or the welcome composer if no chat is open.</summary>
    public void FocusComposer()
    {
        var composer = _chatPanel?.IsVisible == true ? _activeComposer : _welcomeComposer;
        composer?.FocusInput();
    }

    public void PopulateComposerCatalogs(ChatViewModel vm)
    {
        // Build agent, skill, and MCP catalogs from DataStore (accessed via vm)
        var agentChips = vm.GetAgentChips();
        var skillChips = vm.GetSkillChips();
        var mcpChips = vm.GetMcpChips();

        if (_welcomeComposer is not null)
        {
            _welcomeComposer.AvailableAgents = agentChips;
            _welcomeComposer.AvailableSkills = skillChips;
            _welcomeComposer.AvailableMcps = mcpChips;
        }
        if (_activeComposer is not null)
        {
            _activeComposer.AvailableAgents = agentChips;
            _activeComposer.AvailableSkills = skillChips;
            _activeComposer.AvailableMcps = mcpChips;
        }
    }

    private void UpdateComposerAgent(LumiAgent? agent)
    {
        var name = agent?.Name;
        var glyph = agent?.IconGlyph ?? "◉";

        if (_welcomeComposer is not null)
        {
            _welcomeComposer.AgentName = name;
            _welcomeComposer.AgentGlyph = glyph;
        }
        if (_activeComposer is not null)
        {
            _activeComposer.AgentName = name;
            _activeComposer.AgentGlyph = glyph;
        }

        // Update the agent badge in the chat header
        var badge = this.FindControl<Avalonia.Controls.Border>("AgentBadge");
        var badgeText = this.FindControl<Avalonia.Controls.TextBlock>("AgentBadgeText");
        if (badge is not null)
        {
            badge.IsVisible = agent is not null;
            if (badgeText is not null && agent is not null)
                badgeText.Text = $"{agent.IconGlyph} {agent.Name}";
        }
    }

    private void UpdateProjectBadge(ChatViewModel vm)
    {
        var badge = this.FindControl<Avalonia.Controls.Border>("ProjectBadge");
        var badgeText = this.FindControl<Avalonia.Controls.TextBlock>("ProjectBadgeText");
        if (badge is null) return;

        var projectId = vm.CurrentChat?.ProjectId;
        if (projectId.HasValue)
        {
            // Walk up to MainViewModel to look up the project name
            var mainVm = (this.Parent as Control)?.DataContext as MainViewModel
                ?? FindMainViewModel();
            var projectName = mainVm?.GetProjectName(projectId);
            badge.IsVisible = projectName is not null;
            if (badgeText is not null)
                badgeText.Text = $"📁 {projectName}";
        }
        else
        {
            badge.IsVisible = false;
        }
    }

    private MainViewModel? FindMainViewModel()
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        return window?.DataContext as MainViewModel;
    }

    private void UpdateBrowserToggle(ChatViewModel vm)
    {
        var btn = this.FindControl<Button>("BrowserToggleButton");
        if (btn is null) return;

        btn.IsVisible = vm.HasUsedBrowser;

        // Active state: accent pill when browser is open, subtle when closed
        var text = this.FindControl<TextBlock>("BrowserToggleText");
        var isOpen = vm.IsBrowserOpen;

        if (isOpen)
        {
            if (this.TryFindResource("Brush.AccentSubtle", out var bg) && bg is Avalonia.Media.IBrush bgBrush)
                btn.Background = bgBrush;
            if (text is not null && this.TryFindResource("Brush.AccentDefault", out var fg) && fg is Avalonia.Media.IBrush fgBrush)
                text.Foreground = fgBrush;
        }
        else
        {
            btn.ClearValue(Button.BackgroundProperty);
            if (text is not null)
                text.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    /// <summary>Called when the composer's AgentName property changes (user selected via autocomplete).</summary>
    private void OnComposerAgentChanged(StrataChatComposer composer, ChatViewModel vm)
    {
        var agentName = composer.AgentName;

        // Ignore if this matches what's already active (to avoid loops)
        if (vm.ActiveAgent?.Name == agentName) return;

        // Block agent changes once the chat has messages
        if (!vm.CanChangeAgent)
        {
            // Revert the composer back to the current agent
            composer.AgentName = vm.ActiveAgent?.Name;
            composer.AgentGlyph = vm.ActiveAgent?.IconGlyph ?? "◉";
            return;
        }

        if (string.IsNullOrEmpty(agentName))
        {
            vm.SetActiveAgent(null);
        }
        else
        {
            vm.SelectAgentByName(agentName);
            // Sync the other composer
            var other = composer == _welcomeComposer ? _activeComposer : _welcomeComposer;
            if (other is not null)
            {
                other.AgentName = composer.AgentName;
                other.AgentGlyph = composer.AgentGlyph;
            }
        }
    }

    private async Task PickAndAttachFilesAsync(ChatViewModel vm)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.FilePicker_AttachFiles,
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null)
                vm.AddAttachment(path);
        }

        if (files.Count > 0)
        {
            var composer = _activeComposer ?? _welcomeComposer;
            composer?.FocusInput();
        }
    }

    // ── Voice input ──────────────────────────────────────────────

    private async Task ToggleVoiceAsync(StrataChatComposer composer, ChatViewModel vm)
    {
        if (!_voiceService.IsAvailable || _voiceStarting) return;

        if (_voiceService.IsRecording)
        {
            await _voiceService.StopAsync();
            SetComposersRecording(false);
            return;
        }

        _voiceStarting = true;

        // Snapshot current text so we can append voice input
        _textBeforeVoice = vm.PromptText ?? "";
        _lastHypothesis = "";

        _voiceService.HypothesisGenerated += OnVoiceHypothesis;
        _voiceService.ResultGenerated += OnVoiceResult;
        _voiceService.Stopped += OnVoiceStopped;
        _voiceService.Error += OnVoiceError;

        // SpeechRecognizer requires a full BCP-47 tag like "en-US", not just "en"
        var culture = CultureInfo.CurrentUICulture;
        var lang = culture.Name.Contains('-') ? culture.Name : culture.IetfLanguageTag;
        if (string.IsNullOrEmpty(lang) || !lang.Contains('-'))
            lang = "en-US";
        await _voiceService.StartAsync(lang);

        _voiceStarting = false;

        if (_voiceService.IsRecording)
            SetComposersRecording(true);
    }

    private void OnVoiceHypothesis(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribedVm is null) return;
            // Remove previous hypothesis, add new one
            var baseText = _textBeforeVoice;
            if (!string.IsNullOrEmpty(baseText) && !baseText.EndsWith(' '))
                baseText += " ";
            _lastHypothesis = text;
            _subscribedVm.PromptText = baseText + text;
        });
    }

    private void OnVoiceResult(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribedVm is null) return;
            // Commit final text and update the base for the next segment
            var baseText = _textBeforeVoice;
            if (!string.IsNullOrEmpty(baseText) && !baseText.EndsWith(' '))
                baseText += " ";
            _textBeforeVoice = baseText + text;
            _lastHypothesis = "";
            _subscribedVm.PromptText = _textBeforeVoice;
        });
    }

    private void OnVoiceError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_subscribedVm is null) return;
            if (message == "speech_privacy")
                _subscribedVm.StatusText = Loc.Voice_SpeechPrivacyRequired;
            else
                _subscribedVm.StatusText = $"{Loc.Voice_Error}: {message}";
        });
    }

    private void OnVoiceStopped()
    {
        _voiceService.HypothesisGenerated -= OnVoiceHypothesis;
        _voiceService.ResultGenerated -= OnVoiceResult;
        _voiceService.Stopped -= OnVoiceStopped;
        _voiceService.Error -= OnVoiceError;

        Dispatcher.UIThread.Post(() => SetComposersRecording(false));
    }

    private void SetComposersRecording(bool recording)
    {
        if (_welcomeComposer is not null)
            _welcomeComposer.IsRecording = recording;
        if (_activeComposer is not null)
            _activeComposer.IsRecording = recording;
    }

    private void RebuildPendingAttachmentChips(ChatViewModel vm)
    {
        UpdateAttachmentList(_pendingAttachmentList, vm);
        UpdateAttachmentList(_welcomePendingAttachmentList, vm);
    }

    private static void UpdateAttachmentList(StrataAttachmentList? list, ChatViewModel vm)
    {
        if (list is null) return;

        list.Items.Clear();

        foreach (var filePath in vm.PendingAttachments)
        {
            var chip = CreateFileChip(filePath, isRemovable: true);
            chip.RemoveRequested += (_, _) => vm.RemoveAttachment(filePath);
            list.Items.Add(chip);
        }

        list.IsVisible = vm.PendingAttachments.Count > 0;
    }

    private static StrataFileAttachment CreateFileChip(string filePath, bool isRemovable)
    {
        var fileName = Path.GetFileName(filePath);
        string? fileSize = null;
        try
        {
            var info = new FileInfo(filePath);
            if (info.Exists)
                fileSize = FormatFileSize(info.Length);
        }
        catch { /* ignore */ }

        var chip = new StrataFileAttachment
        {
            FileName = fileName,
            FileSize = fileSize,
            Status = StrataAttachmentStatus.Completed,
            IsRemovable = isRemovable
        };

        chip.OpenRequested += (_, _) => OpenFileInSystem(filePath);
        return chip;
    }

    private static void OpenFileInSystem(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch { /* ignore if file doesn't exist */ }
    }

    // ── Drag & Drop ──────────────────────────────────────────────

#pragma warning disable CS0618 // DragEventArgs.Data / DataFormats.Files — new IDataTransfer API lacks GetFiles()
    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
            if (_dropOverlay is not null) _dropOverlay.IsVisible = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (_dropOverlay is not null) _dropOverlay.IsVisible = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_dropOverlay is not null) _dropOverlay.IsVisible = false;

        if (DataContext is not ChatViewModel vm) return;

        var files = e.Data.GetFiles();
        if (files is null) return;

        foreach (var item in files)
        {
            var path = item.TryGetLocalPath();
            if (path is not null)
                vm.AddAttachment(path);
        }

        // Focus the composer after dropping files
        var composer = _chatPanel?.IsVisible == true ? _activeComposer : _welcomeComposer;
        composer?.FocusInput();
    }
#pragma warning restore CS0618

    private static string GetToolGlyph(string toolName)
    {
        return toolName switch
        {
            "powershell" or "run_in_terminal" or "bash" or "shell" => "⌨",
            "create" or "write_file" or "create_file" or "edit" or "edit_file" or "str_replace" or "insert"
                or "replace_string_in_file" or "multi_replace_string_in_file" or "str_replace_editor" => "📝",
            "view" or "read_file" or "read" => "📄",
            "browser" or "browser_navigate" or "browser_do" or "browser_look" or "browser_js" => "🌐",
            "lumi_search" or "web_search" or "search" => "🔎",
            "web_fetch" or "lumi_fetch" => "📚",
            "ui_inspect" or "ui_find" or "ui_click" or "ui_type" or "ui_read" => "🖥",
            "save_memory" or "update_memory" or "recall_memory" or "delete_memory" => "🧠",
            "update_todo" or "manage_todo_list" => "✅",
            _ => "⚙"
        };
    }

    /// <summary>Returns true if the tool is a file-edit tool that can show a diff.</summary>
    private static bool IsFileEditTool(string toolName)
        => toolName is "edit" or "edit_file" or "str_replace" or "str_replace_editor"
            or "replace_string_in_file" or "insert" or "create" or "write_file"
            or "create_file" or "create_and_write_file" or "write" or "save_file"
            or "multi_replace_string_in_file";

    /// <summary>Extracts diff data (filePath, oldText, newText) from tool call args JSON.</summary>
    private static (string FilePath, string? OldText, string? NewText)? ExtractDiffData(string toolName, string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            if (toolName is "multi_replace_string_in_file")
            {
                // For multi-replace, extract the first replacement as the primary diff
                if (root.TryGetProperty("replacements", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var fp = item.TryGetProperty("filePath", out var fpVal) ? fpVal.GetString()
                            : item.TryGetProperty("path", out var pVal) ? pVal.GetString() : null;
                        var old = item.TryGetProperty("oldString", out var osVal) ? osVal.GetString() : null;
                        var nw = item.TryGetProperty("newString", out var nsVal) ? nsVal.GetString() : null;
                        if (fp is not null) return (fp, old, nw);
                    }
                }
                return null;
            }

            // Standard edit tools: filePath/path + oldString + newString
            var filePath = root.TryGetProperty("filePath", out var f) ? f.GetString()
                : root.TryGetProperty("path", out var p) ? p.GetString()
                : root.TryGetProperty("file", out var fi) ? fi.GetString() : null;

            if (filePath is null) return null;

            var oldText = root.TryGetProperty("oldString", out var o) ? o.GetString()
                : root.TryGetProperty("old_str", out var os) ? os.GetString() : null;
            var newText = root.TryGetProperty("newString", out var n) ? n.GetString()
                : root.TryGetProperty("new_str", out var ns) ? ns.GetString()
                : root.TryGetProperty("content", out var c) ? c.GetString()
                : root.TryGetProperty("insert_text", out var it) ? it.GetString() : null;

            return (filePath, oldText, newText);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts all file diffs from tool call args JSON.
    /// For multi_replace_string_in_file, returns one entry per replacement.
    /// For standard edit tools, returns a single entry.
    /// </summary>
    private static List<(string FilePath, string? OldText, string? NewText)> ExtractAllDiffs(string toolName, string? argsJson)
    {
        var results = new List<(string FilePath, string? OldText, string? NewText)>();
        if (string.IsNullOrWhiteSpace(argsJson)) return results;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            if (toolName is "multi_replace_string_in_file")
            {
                if (root.TryGetProperty("replacements", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var fp = item.TryGetProperty("filePath", out var fpVal) ? fpVal.GetString()
                            : item.TryGetProperty("path", out var pVal) ? pVal.GetString() : null;
                        if (fp is null) continue;
                        var old = item.TryGetProperty("oldString", out var osVal) ? osVal.GetString() : null;
                        var nw = item.TryGetProperty("newString", out var nsVal) ? nsVal.GetString() : null;
                        results.Add((fp, old, nw));
                    }
                }
                return results;
            }

            // Single edit: use ExtractDiffData
            var diff = ExtractDiffData(toolName, argsJson);
            if (diff is not null) results.Add(diff.Value);
        }
        catch { }
        return results;
    }

    private static List<TodoStepSnapshot> ParseTodoSteps(string? argsJson)
    {
        var result = new List<TodoStepSnapshot>();
        if (string.IsNullOrWhiteSpace(argsJson))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("todos", out var todosText)
                    && todosText.ValueKind == JsonValueKind.String)
                {
                    var parsed = ParseTodoChecklist(todosText.GetString());
                    if (parsed.Count > 0)
                        return parsed;
                }
            }

            JsonElement list = default;
            var hasList = (root.ValueKind == JsonValueKind.Object
                           && (root.TryGetProperty("todoList", out list)
                               || root.TryGetProperty("todo", out list)
                               || root.TryGetProperty("items", out list)
                               || root.TryGetProperty("tasks", out list)
                               || root.TryGetProperty("todos", out list)))
                          || root.ValueKind == JsonValueKind.Array;

            if (!hasList)
                return result;

            if (root.ValueKind == JsonValueKind.Array)
                list = root;

            if (list.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var title = GetString(item, "title")
                            ?? GetString(item, "step")
                            ?? GetString(item, "name")
                            ?? GetString(item, "label")
                            ?? string.Empty;

                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var status = GetString(item, "status")
                             ?? GetString(item, "state")
                             ?? "not-started";

                var id = 0;
                if (item.TryGetProperty("id", out var idEl))
                {
                    if (idEl.ValueKind == JsonValueKind.Number)
                        id = idEl.GetInt32();
                    else if (idEl.ValueKind == JsonValueKind.String)
                        _ = int.TryParse(idEl.GetString(), out id);
                }

                result.Add(new TodoStepSnapshot
                {
                    Id = id,
                    Title = title,
                    Status = status
                });
            }
        }
        catch
        {
            // ignore invalid todo payload
        }

        return result;
    }

    private static List<TodoStepSnapshot> ParseTodoChecklist(string? checklist)
    {
        var result = new List<TodoStepSnapshot>();
        if (string.IsNullOrWhiteSpace(checklist))
            return result;

        var lines = checklist.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var index = 1;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            bool isDone;
            string title;

            if (line.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase))
            {
                isDone = true;
                title = line[5..].Trim();
            }
            else if (line.StartsWith("- [ ]", StringComparison.OrdinalIgnoreCase))
            {
                isDone = false;
                title = line[5..].Trim();
            }
            else
            {
                // Fallback: treat non-checkbox line as a pending task
                isDone = false;
                title = line.TrimStart('-', '*', ' ').Trim();
            }

            if (string.IsNullOrWhiteSpace(title))
                continue;

            result.Add(new TodoStepSnapshot
            {
                Id = index++,
                Title = title,
                Status = isDone ? "completed" : "in-progress"
            });
        }

        return result;
    }

    private void UpsertTodoProgressToolCall(IReadOnlyList<TodoStepSnapshot> steps, string toolStatus)
    {
        if (_currentToolGroupStack is null)
            return;

        var total = steps.Count;
        var completed = steps.Count(static s => string.Equals(s.Status, "completed", StringComparison.OrdinalIgnoreCase));
        var failed = steps.Count(static s => string.Equals(s.Status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Status, "blocked", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Status, "cancelled", StringComparison.OrdinalIgnoreCase));

        _currentTodoProgress = new TodoProgressState
        {
            Total = total,
            Completed = completed,
            Failed = failed,
            ToolStatus = toolStatus
        };

        if (_currentTodoToolCall is null)
        {
            _currentTodoToolCall = new StrataAiToolCall
            {
                ToolName = $"{GetToolGlyph("update_todo")} {Loc.ToolTodo_Title}",
                Status = toolStatus switch
                {
                    "Completed" => StrataAiToolCallStatus.Completed,
                    "Failed" => StrataAiToolCallStatus.Failed,
                    _ => StrataAiToolCallStatus.InProgress
                },
                IsExpanded = false,
                InputParameters = BuildTodoDetailsMarkdown(steps),
                MoreInfo = null
            };
            _currentToolGroupStack.Children.Add(_currentTodoToolCall);
            _currentToolGroupCount++;
            return;
        }

        _currentTodoToolCall.Status = toolStatus switch
        {
            "Completed" => StrataAiToolCallStatus.Completed,
            "Failed" => StrataAiToolCallStatus.Failed,
            _ => StrataAiToolCallStatus.InProgress
        };
        _currentTodoToolCall.InputParameters = BuildTodoDetailsMarkdown(steps);
        _currentTodoToolCall.MoreInfo = null;
    }

    private static string BuildTodoDetailsMarkdown(IReadOnlyList<TodoStepSnapshot> steps)
    {
        if (steps.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var step in steps)
        {
            var isDone = string.Equals(step.Status, "completed", StringComparison.OrdinalIgnoreCase);
            sb.Append("- ")
              .Append(isDone ? "[x] " : "[ ] ")
              .AppendLine(step.Title);
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => string.Format(Loc.FileSize_B, bytes),
            < 1024 * 1024 => string.Format(Loc.FileSize_KB, $"{bytes / 1024.0:F1}"),
            < 1024 * 1024 * 1024 => string.Format(Loc.FileSize_MB, $"{bytes / (1024.0 * 1024):F1}"),
            _ => string.Format(Loc.FileSize_GB, $"{bytes / (1024.0 * 1024 * 1024):F2}")
        };
    }

    private static string? FormatToolArgs(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return JsonSerializer.Serialize(doc, AppDataJsonContext.Default.JsonDocument);
        }
        catch
        {
            return argsJson;
        }
    }

    /// <summary>
    /// Formats tool arguments into a human-readable, non-technical summary.
    /// Known tools get tailored labels; unknown tools get clean key → value lines.
    /// </summary>
    private static string? FormatToolArgsFriendly(string toolName, string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            switch (toolName)
            {
                case "web_fetch":
                {
                    var url = GetString(root, "url");
                    if (url is null) break;
                    return $"**URL:** {url}";
                }

                case "view":
                {
                    var path = GetString(root, "path");
                    if (path is null) break;
                    var fileName = Path.GetFileName(path);
                    var dir = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(Path.GetExtension(path)))
                        return $"**Path:** `{path}`";
                    var sb = new StringBuilder();
                    sb.AppendLine($"**File:** `{fileName}`");
                    if (!string.IsNullOrEmpty(dir))
                        sb.AppendLine($"**Location:** `{dir}`");
                    return sb.ToString().TrimEnd();
                }

                case "powershell":
                {
                    var cmd = GetString(root, "command");
                    if (string.IsNullOrEmpty(cmd)) break;
                    return $"```powershell\n{cmd.Trim()}\n```";
                }

                case "report_intent":
                {
                    var intent = GetString(root, "intent");
                    return intent is not null ? $"Intent: {intent}" : null;
                }

                case "read_powershell":
                case "stop_powershell":
                    return null; // No useful detail to show

                case "create":
                {
                    var path = GetString(root, "path");
                    if (path is null) break;
                    var fileName = Path.GetFileName(path);
                    var dir = Path.GetDirectoryName(path);
                    var sb = new StringBuilder();
                    sb.AppendLine($"**File:** `{fileName}`");
                    if (!string.IsNullOrEmpty(dir))
                        sb.AppendLine($"**Location:** `{dir}`");
                    return sb.ToString().TrimEnd();
                }

                case "edit":
                case "edit_file":
                case "str_replace":
                case "str_replace_editor":
                case "replace_string_in_file":
                case "insert":
                {
                    var path = GetString(root, "filePath") ?? GetString(root, "path");
                    if (path is null) break;
                    var fileName = Path.GetFileName(path);
                    var dir = Path.GetDirectoryName(path);
                    var sb = new StringBuilder();
                    sb.AppendLine($"**File:** `{fileName}`");
                    if (!string.IsNullOrEmpty(dir))
                        sb.AppendLine($"**Location:** `{dir}`");
                    return sb.ToString().TrimEnd();
                }

                case "multi_replace_string_in_file":
                {
                    if (root.TryGetProperty("replacements", out var arr)
                        && arr.ValueKind == JsonValueKind.Array)
                    {
                        var count = arr.GetArrayLength();
                        string? firstFile = null;
                        foreach (var item in arr.EnumerateArray())
                        {
                            if (item.TryGetProperty("filePath", out var fpVal))
                            { firstFile = Path.GetFileName(fpVal.GetString()); break; }
                        }
                        if (firstFile is not null)
                            return count > 1
                                ? $"**File:** `{firstFile}` (+{count - 1} more)"
                                : $"**File:** `{firstFile}`";
                    }
                    break;
                }

                case "save_memory":
                {
                    var key = GetString(root, "key");
                    var content = GetString(root, "content");
                    if (key is null) break;
                    var result = $"**Key:** {key}";
                    if (content is not null)
                        result += $"\n**Content:** {(content.Length > 120 ? content[..120] + "\u2026" : content)}";
                    return result;
                }

                case "update_memory":
                {
                    var key = GetString(root, "key");
                    var content = GetString(root, "content");
                    var newKey = GetString(root, "newKey");
                    if (key is null) break;
                    var result = $"**Key:** {key}";
                    if (newKey is not null) result += $"\n**New key:** {newKey}";
                    if (content is not null)
                        result += $"\n**Content:** {(content.Length > 120 ? content[..120] + "\u2026" : content)}";
                    return result;
                }

                case "delete_memory":
                case "recall_memory":
                {
                    var key = GetString(root, "key");
                    return key is not null ? $"**Key:** {key}" : null;
                }
            }

            // Generic fallback: clean key → value display with friendly labels
            return FormatGenericArgs(root);
        }
        catch
        {
            return argsJson;
        }
    }

    /// <summary>Formats unknown tool args as clean "Label: value" lines.</summary>
    private static string? FormatGenericArgs(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return root.ToString();

        var sb = new StringBuilder();
        foreach (var prop in root.EnumerateObject())
        {
            var label = FriendlyFieldName(prop.Name);
            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.True => Loc.Bool_Yes,
                JsonValueKind.False => Loc.Bool_No,
                JsonValueKind.Number => prop.Value.ToString(),
                _ => prop.Value.ToString()
            };

            // Truncate very long values
            if (value.Length > 200)
                value = value[..197] + "...";

            sb.AppendLine($"**{label}:** {value}");
        }

        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }

    /// <summary>Turns "camelCase" or "snake_case" field names into "Camel case" display labels.</summary>
    private static string FriendlyFieldName(string fieldName)
    {
        // Map common programmer field names to friendly labels
        return fieldName switch
        {
            "url" => Loc.FieldLabel_URL,
            "filePath" or "file_path" => Loc.FieldLabel_File,
            "path" => Loc.FieldLabel_Path,
            "query" => Loc.FieldLabel_SearchQuery,
            "command" => Loc.FieldLabel_Command,
            "description" => Loc.FieldLabel_Description,
            "initial_wait" => Loc.FieldLabel_Timeout,
            "intent" => Loc.FieldLabel_Intent,
            "content" => Loc.FieldLabel_Content,
            "text" => Loc.FieldLabel_Text,
            "language" => Loc.FieldLabel_Language,
            "timeout" => Loc.FieldLabel_Timeout,
            "args" or "arguments" => Loc.FieldLabel_Arguments,
            "input" => Loc.FieldLabel_Input,
            "output" => Loc.FieldLabel_Output,
            "name" => Loc.FieldLabel_Name,
            "type" => Loc.FieldLabel_Type,
            "format" => Loc.FieldLabel_Format,
            "limit" => Loc.FieldLabel_Limit,
            "offset" => Loc.FieldLabel_StartAt,
            "count" => Loc.FieldLabel_Count,
            _ => CapitalizeFirst(fieldName.Replace('_', ' '))
        };
    }

    private static string CapitalizeFirst(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Maps a tool call to a user-friendly display name and summary line.
    /// Non-coders see human-readable labels instead of raw tool names and JSON.
    /// </summary>
    private static (string Name, string? Info) GetFriendlyToolDisplay(string toolName, string? author, string? argsJson)
    {
        switch (toolName)
        {
            case "web_fetch":
            case "lumi_fetch":
            {
                var url = ExtractJsonField(argsJson, "url");
                string? domain = null;
                if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    domain = uri.Host;
                return (Loc.Tool_ReadingWebsite, domain ?? url);
            }

            case "lumi_search":
            {
                var query = ExtractJsonField(argsJson, "query");
                return (Loc.Tool_SearchingWeb, query);
            }

            case "view":
            {
                var path = ExtractJsonField(argsJson, "path");
                if (path is null) return (Loc.Tool_ReadingFile, null);

                var ext = Path.GetExtension(path);
                var fileName = Path.GetFileName(path);

                // No extension likely means a directory listing
                if (string.IsNullOrEmpty(ext))
                    return (Loc.Tool_BrowsingFolder, fileName);

                // Rich documents
                if (ext is ".docx" or ".doc" or ".pdf" or ".pptx" or ".ppt" or ".xlsx" or ".xls" or ".rtf")
                    return (Loc.Tool_ReadingDocument, fileName);

                return (Loc.Tool_ReadingFile, fileName);
            }

            case "powershell":
            {
                // Use description as tool name if available
                var desc = ExtractJsonField(argsJson, "description");
                if (!string.IsNullOrWhiteSpace(desc))
                    return (desc, null);

                // Fall back: derive a short summary from the command itself
                var cmd = ExtractJsonField(argsJson, "command");
                var summary = SummarizeCommand(cmd);
                return (summary ?? Loc.Tool_RunningCommand, null);
            }

            case "report_intent":
                return (Loc.Tool_Planning, ExtractJsonField(argsJson, "intent"));

            case "read_powershell":
                return (Loc.Tool_ReadingTerminal, null);

            case "stop_powershell":
                return (Loc.Tool_StoppingCommand, null);

            case "create":
            {
                var path = ExtractJsonField(argsJson, "path");
                var fileName = path is not null ? Path.GetFileName(path) : null;
                return (Loc.Tool_CreatingFile, fileName);
            }

            case "edit":
            case "edit_file":
            case "str_replace":
            case "str_replace_editor":
            case "replace_string_in_file":
            case "insert":
            {
                var path = ExtractJsonField(argsJson, "filePath")
                    ?? ExtractJsonField(argsJson, "path");
                var fileName = path is not null ? Path.GetFileName(path) : null;
                return (Loc.Tool_EditingFile, fileName);
            }

            case "multi_replace_string_in_file":
            {
                // Try to extract file name from first replacement
                string? fileName = null;
                try
                {
                    if (argsJson is not null)
                    {
                        using var doc = JsonDocument.Parse(argsJson);
                        if (doc.RootElement.TryGetProperty("replacements", out var arr)
                            && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in arr.EnumerateArray())
                            {
                                var fp = item.TryGetProperty("filePath", out var fpVal) ? fpVal.GetString() : null;
                                if (fp is not null) { fileName = Path.GetFileName(fp); break; }
                            }
                        }
                    }
                }
                catch { }
                return (Loc.Tool_EditingFile, fileName);
            }

            case "save_memory":
                return (Loc.Tool_Remembering, ExtractJsonField(argsJson, "key"));
            case "update_memory":
                return (Loc.Tool_UpdatingMemory, ExtractJsonField(argsJson, "key"));
            case "delete_memory":
                return (Loc.Tool_Forgetting, ExtractJsonField(argsJson, "key"));
            case "recall_memory":
                return (Loc.Tool_Recalling, ExtractJsonField(argsJson, "key"));

            case "fetch_skill":
                return (Loc.Tool_FetchingSkill, ExtractJsonField(argsJson, "name"));

            case "ask_question":
                return (Loc.Tool_AskingQuestion, ExtractJsonField(argsJson, "question"));

            case "browser":
            {
                var url = ExtractJsonField(argsJson, "url");
                string? domain = null;
                if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var navUri))
                    domain = navUri.Host;
                return (Loc.Tool_OpeningPage, domain ?? url);
            }
            case "browser_look":
                return (Loc.Tool_BrowserSnapshot, ExtractJsonField(argsJson, "filter"));
            case "browser_do":
            {
                var action = ExtractJsonField(argsJson, "action")?.ToLowerInvariant();
                var target = ExtractJsonField(argsJson, "target");
                return action switch
                {
                    "click" => (Loc.Tool_ClickingElement, target),
                    "type" => (Loc.Tool_TypingText, target),
                    "press" => (Loc.Tool_Action, target),
                    "scroll" => (Loc.Tool_BrowserScroll, target),
                    "select" => (Loc.Tool_BrowserSelect, target),
                    "back" => (Loc.Tool_BrowserBack, null),
                    "download" => (Loc.Tool_ReadingFile, target),
                    "wait" => (Loc.Tool_BrowserWait, target),
                    _ => (Loc.Tool_Action, action)
                };
            }
            case "browser_js":
                return (Loc.Tool_BrowserEvaluate, null);

            case "ui_list_windows":
                return (Loc.Tool_ListingWindows, null);
            case "ui_inspect":
                return (Loc.Tool_InspectingWindow, ExtractJsonField(argsJson, "title"));
            case "ui_find":
                return (Loc.Tool_FindingElement, ExtractJsonField(argsJson, "query"));
            case "ui_click":
                return (Loc.Tool_ClickingControl, ExtractJsonField(argsJson, "elementId"));
            case "ui_type":
                return (Loc.Tool_TypingInControl, ExtractJsonField(argsJson, "elementId"));
            case "ui_press_keys":
                return (Loc.Tool_PressingKeys, ExtractJsonField(argsJson, "keys"));
            case "ui_read":
                return (Loc.Tool_ReadingControl, ExtractJsonField(argsJson, "elementId"));

            default:
                var displayName = author ?? FormatToolNameFriendly(toolName);
                var info = ExtractToolSummary(toolName, argsJson);
                return (displayName, info);
        }
    }

    /// <summary>Derives a short human-readable summary from a raw PowerShell command.</summary>
    private static string? SummarizeCommand(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;

        var firstLine = cmd.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => !l.TrimStart().StartsWith('#'))?.Trim();
        if (firstLine is null) return null;

        // Match common cmdlet patterns
        if (firstLine.StartsWith("Get-Content", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_ReadingFileContents;
        if (firstLine.StartsWith("Get-ChildItem", StringComparison.OrdinalIgnoreCase)
            || firstLine.StartsWith("dir ", StringComparison.OrdinalIgnoreCase)
            || firstLine.StartsWith("ls ", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_ListingFiles;
        if (firstLine.StartsWith("Copy-Item", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_CopyingFiles;
        if (firstLine.StartsWith("Remove-Item", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_CleaningUp;
        if (firstLine.StartsWith("Expand-Archive", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_ExtractingArchive;
        if (firstLine.StartsWith("Get-Command", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_CheckingTools;
        if (firstLine.StartsWith("Install-", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_InstallingPackage;
        if (firstLine.StartsWith("pip install", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_InstallingPython;
        if (firstLine.StartsWith("npm install", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_InstallingNpm;
        if (firstLine.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_NavigatingDirs;
        if (firstLine.Contains("New-Object -ComObject Word", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_OpeningWord;
        if (firstLine.Contains("New-Object -ComObject PowerPoint", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_OpeningPowerPoint;
        if (firstLine.Contains("New-Object -ComObject Excel", StringComparison.OrdinalIgnoreCase))
            return Loc.Cmd_OpeningExcel;

        return null;
    }

    /// <summary>Converts snake_case or dot.separated tool names to Title Case.</summary>
    private static string FormatToolNameFriendly(string toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return Loc.Tool_Action;

        var cleaned = toolName.Replace('_', ' ').Replace('.', ' ').Trim();
        if (cleaned.Length == 0) return Loc.Tool_Action;

        // Capitalize first letter
        return char.ToUpper(cleaned[0]) + cleaned[1..];
    }

    /// <summary>Extracts a single string field from a JSON object.</summary>
    private static string? ExtractJsonField(string? json, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(fieldName, out var val) ? val.GetString() : null;
        }
        catch { return null; }
    }

    private static string ExtractDomain(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host.Replace("www.", "");
        return url;
    }

    private static Control BuildSkillChips(List<SkillReference> skills)
    {
        var app = Application.Current;
        var wrap = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var skill in skills)
        {
            var chip = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(10),
                Padding = new Avalonia.Thickness(8, 3, 10, 3),
                Margin = new Avalonia.Thickness(0, 0, 4, 0),
                Background = app?.FindResource("Brush.Surface2") as IBrush
                    ?? Brushes.Transparent,
                BorderBrush = app?.FindResource("Brush.BorderSubtle") as IBrush
                    ?? Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(1),
                Child = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = skill.Glyph,
                            FontSize = 11,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = skill.Name,
                            FontSize = 11,
                            Foreground = app?.FindResource("Brush.TextSecondary") as IBrush
                                ?? Brushes.Gray,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                        }
                    }
                }
            };
            wrap.Children.Add(chip);
        }
        return wrap;
    }

    private static Control BuildSourcesSection(List<SearchSource> sources)
    {
        // Deduplicate by URL
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<SearchSource>();
        foreach (var s in sources)
        {
            if (seen.Add(s.Url))
                unique.Add(s);
        }

        var count = unique.Count;
        var label = count == 1 ? Loc.Sources_One : string.Format(Loc.Sources_N, count);

        var sourcesList = new StackPanel { Spacing = 2 };
        foreach (var src in unique)
        {
            var domain = ExtractDomain(src.Url);

            var link = new Button
            {
                Classes = { "subtle" },
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Padding = new Thickness(6, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = src.Title,
                            FontSize = 12,
                            FontWeight = Avalonia.Media.FontWeight.Medium,
                            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                            MaxLines = 1,
                        },
                        new TextBlock
                        {
                            Text = domain,
                            FontSize = 11,
                            Opacity = 0.55,
                            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                        }
                    }
                }
            };

            var url = src.Url;
            link.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { }
            };

            sourcesList.Children.Add(link);
        }

        return new StrataThink
        {
            Label = label,
            IsExpanded = false,
            Content = sourcesList,
            Margin = new Thickness(0, 4, 0, 0),
        };
    }

    /// <summary>Builds a "Changes" section shown at the bottom of an assistant message, similar to sources.</summary>
    private Control BuildFileChangesSection(List<(string FilePath, string ToolName, string? OldText, string? NewText)> edits)
    {
        // Deduplicate by file path (keep last edit per file)
        var seen = new Dictionary<string, (string FilePath, string ToolName, string? OldText, string? NewText)>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
            seen[edit.FilePath] = edit;

        var unique = seen.Values.ToList();
        var count = unique.Count;
        var label = count == 1 ? Loc.FileChanges_One : string.Format(Loc.FileChanges_N, count);

        var changesList = new StackPanel { Spacing = 2 };
        foreach (var edit in unique)
        {
            var fileName = System.IO.Path.GetFileName(edit.FilePath);
            var dir = System.IO.Path.GetDirectoryName(edit.FilePath);

            var isCreate = edit.ToolName is "create" or "write_file" or "create_file" or "write" or "save_file" or "create_and_write_file";
            var actionIcon = isCreate ? "📄" : "📝";
            var actionLabel = isCreate ? Loc.FileChange_Created : Loc.FileChange_Modified;

            var chip = new Button
            {
                Classes = { "subtle" },
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Padding = new Thickness(6, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,6,*"),
                    Children =
                    {
                        SetColumn(new TextBlock
                        {
                            Text = actionIcon,
                            FontSize = 13,
                            VerticalAlignment = VerticalAlignment.Center,
                        }, 0),
                        SetColumn(new StackPanel
                        {
                            Spacing = 1,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = fileName,
                                    FontSize = 12,
                                    FontWeight = Avalonia.Media.FontWeight.Medium,
                                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                                    MaxLines = 1,
                                },
                                new TextBlock
                                {
                                    Text = actionLabel + (dir is not null ? $" · {dir}" : ""),
                                    FontSize = 11,
                                    Opacity = 0.55,
                                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                                    MaxLines = 1,
                                }
                            }
                        }, 2)
                    }
                }
            };

            var capturedEdit = edit;
            chip.Click += (_, _) =>
            {
                if (DataContext is ChatViewModel chatVm)
                    chatVm.ShowDiff(capturedEdit.FilePath, capturedEdit.OldText, capturedEdit.NewText);
            };

            changesList.Children.Add(chip);
        }

        return new StrataThink
        {
            Label = label,
            IsExpanded = false,
            Content = changesList,
            Margin = new Thickness(0, 4, 0, 0),
        };
    }

    /// <summary>Helper to set Grid.Column and return the control for fluent use in initializers.</summary>
    private static T SetColumn<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static string? ExtractToolSummary(string? toolName, string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("url", out var url))
                return url.GetString();
            if (root.TryGetProperty("path", out var path))
                return path.GetString();
            if (root.TryGetProperty("filePath", out var filePath))
                return filePath.GetString();
            if (root.TryGetProperty("query", out var query))
                return query.GetString();
            if (root.TryGetProperty("command", out var cmd))
                return cmd.GetString();

            return null;
        }
        catch
        {
            return null;
        }
    }
}

