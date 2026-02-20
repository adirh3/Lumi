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
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Models;
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

    // Tool grouping state
    private StrataThink? _currentToolGroup;
    private StackPanel? _currentToolGroupStack;
    private int _currentToolGroupCount;

    // Typing indicator
    private StrataTypingIndicator? _typingIndicator;

    // Track files already shown as chips to avoid duplicates
    private readonly HashSet<string> _shownFilePaths = new(StringComparer.OrdinalIgnoreCase);

    // Files created during tool execution, to be shown after the assistant message
    private readonly List<string> _pendingToolFileChips = [];

    // Track tool call start times for duration display
    private readonly Dictionary<string, long> _toolStartTimes = [];

    // Track the current intent text from report_intent for friendly group labels
    private string? _currentIntentText;

    // Track if we've already wired up event handlers
    private ChatViewModel? _subscribedVm;
    private SettingsViewModel? _settingsVm;

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

        // Wire file-created-by-tool to show attachment chips in the transcript
        vm.FileCreatedByTool += filePath =>
        {
            if (_shownFilePaths.Add(filePath))
                _pendingToolFileChips.Add(filePath);
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
                RebuildMessageStack(vm);

                // Update project badge
                UpdateProjectBadge(vm);

                if (hasChat)
                    _chatShell?.ResetAutoScroll();
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
                _currentIntentText = null;
                _shownFilePaths.Clear();
                _pendingToolFileChips.Clear();
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
            RebuildMessageStack(_subscribedVm);
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

    private void RebuildMessageStack(ChatViewModel vm)
    {
        // Replace the transcript content with a StackPanel we control
        _messageStack = new StackPanel { Spacing = 12 };
        _currentToolGroup = null;
        _currentToolGroupStack = null;
        _currentToolGroupCount = 0;
        _currentIntentText = null;
        _shownFilePaths.Clear();
        _pendingToolFileChips.Clear();
        _toolStartTimes.Clear();
        if (_chatShell is not null)
            _chatShell.Transcript = _messageStack;

        foreach (var msgVm in vm.Messages)
            AddMessageControl(msgVm);

        CloseToolGroup();
        CollapseAllCompletedTurns();

        // Scroll to bottom after loading chat history
        if (vm.CurrentChat is not null)
            Dispatcher.UIThread.Post(() => _chatShell?.ScrollToEnd(), DispatcherPriority.Loaded);
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

            // Skip internal polling/control tools — they clutter the UI
            if (toolName is "read_powershell" or "stop_powershell")
                return;

            // announce_file: don't show a tool card — collect the file for attachment chip
            if (toolName == "announce_file")
            {
                var filePath = ExtractJsonField(msgVm.Content, "filePath");
                if (filePath is not null && File.Exists(filePath) && _shownFilePaths.Add(filePath))
                    _pendingToolFileChips.Add(filePath);
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
                        var isLive = msgVm.ToolStatus is not "Completed" and not "Failed";
                        if (_currentToolGroup is null)
                        {
                            _currentToolGroupStack = new StackPanel { Spacing = 4 };
                            _currentToolGroupCount = 0;
                            _currentToolGroup = new StrataThink
                            {
                                Label = isLive ? intentText + "\u2026" : intentText,
                                IsExpanded = false,
                                IsActive = isLive,
                                Content = _currentToolGroupStack
                            };
                            InsertBeforeTypingIndicator(_currentToolGroup);
                        }
                        else
                        {
                            _currentToolGroup.Label = isLive ? intentText + "\u2026" : intentText;
                        }
                    }
                }
                return;
            }

            if (!showToolCalls)
                return;

            var initialStatus = msgVm.ToolStatus switch
            {
                "Completed" => StrataAiToolCallStatus.Completed,
                "Failed" => StrataAiToolCallStatus.Failed,
                _ => StrataAiToolCallStatus.InProgress
            };

            var (friendlyName, friendlyInfo) = GetFriendlyToolDisplay(toolName, msgVm.Author, msgVm.Content);

            var toolCall = new StrataAiToolCall
            {
                ToolName = friendlyName,
                Status = initialStatus,
                IsExpanded = false,
                InputParameters = FormatToolArgsFriendly(toolName, msgVm.Content),
                MoreInfo = friendlyInfo
            };

            // Track start time for duration calculation (live sessions only)
            var toolCallId = msgVm.Message.ToolCallId;
            if (toolCallId is not null && initialStatus == StrataAiToolCallStatus.InProgress)
                _toolStartTimes[toolCallId] = Stopwatch.GetTimestamp();

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
            if (_currentToolGroup is null)
            {
                _currentToolGroupStack = new StackPanel { Spacing = 4 };
                _currentToolGroupCount = 0;
                _currentToolGroup = new StrataThink
                {
                    Label = _currentIntentText is not null
                        ? _currentIntentText + "\u2026"
                        : Loc.ToolGroup_Working,
                    IsExpanded = false,
                    IsActive = initialStatus == StrataAiToolCallStatus.InProgress,
                    Content = _currentToolGroupStack
                };
                InsertBeforeTypingIndicator(_currentToolGroup);
            }

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

            // For user messages with attachments, wrap content + attachments in a StackPanel
            object msgContent;
            if (isUser && msgVm.Message.Attachments.Count > 0)
            {
                var contentStack = new StackPanel { Spacing = 6 };
                contentStack.Children.Add(md);

                var attachList = new StrataAttachmentList { ShowAddButton = false };
                foreach (var filePath in msgVm.Message.Attachments)
                {
                    var chip = CreateFileChip(filePath, isRemovable: false);
                    attachList.Items.Add(chip);
                }
                contentStack.Children.Add(attachList);
                msgContent = contentStack;
            }
            else if (!isUser && _pendingToolFileChips.Count > 0)
            {
                // Attach files created by tools to the assistant message
                var contentStack = new StackPanel { Spacing = 6 };
                contentStack.Children.Add(md);

                var attachList = new StrataAttachmentList { ShowAddButton = false };
                foreach (var filePath in _pendingToolFileChips)
                {
                    var chip = CreateFileChip(filePath, isRemovable: false);
                    attachList.Items.Add(chip);
                }
                contentStack.Children.Add(attachList);
                _pendingToolFileChips.Clear();
                msgContent = contentStack;
            }
            else
            {
                msgContent = md;
            }

            var msg = new StrataChatMessage
            {
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
            _currentIntentText = null;
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
        var blocksToMerge = new List<StrataThink>();
        for (int i = idx - 1; i >= 0; i--)
        {
            if (_messageStack.Children[i] is StrataThink think)
                blocksToMerge.Add(think);
            else
                break;
        }

        if (blocksToMerge.Count < 2) return;

        blocksToMerge.Reverse();

        // Count items for summary label
        int totalToolCalls = 0;
        int failedCount = 0;
        bool hasReasoning = false;

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
        if (hasReasoning && totalToolCalls > 0)
            label = string.Format(Loc.TurnSummary_ReasonedAndActions, totalToolCalls);
        else if (totalToolCalls > 0)
            label = totalToolCalls == 1
                ? Loc.ToolGroup_Finished
                : string.Format(Loc.ToolGroup_FinishedCount, totalToolCalls);
        else
            label = Loc.Tool_ReasoningLabel;

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
            IsExpanded = false,
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
            }
        }

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
        }
    }

    private static void SyncModelFromComposer(StrataChatComposer composer, ChatViewModel vm)
    {
        var selected = composer.SelectedModel?.ToString();
        if (!string.IsNullOrEmpty(selected) && selected != vm.SelectedModel)
            vm.SelectedModel = selected;
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
        // Build agent catalog from DataStore (accessed via vm)
        var agentChips = vm.GetAgentChips();
        var skillChips = vm.GetSkillChips();

        if (_welcomeComposer is not null)
        {
            _welcomeComposer.AvailableAgents = agentChips;
            _welcomeComposer.AvailableSkills = skillChips;
        }
        if (_activeComposer is not null)
        {
            _activeComposer.AvailableAgents = agentChips;
            _activeComposer.AvailableSkills = skillChips;
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
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
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

            case "save_memory":
                return (Loc.Tool_Remembering, ExtractJsonField(argsJson, "key"));
            case "update_memory":
                return (Loc.Tool_UpdatingMemory, ExtractJsonField(argsJson, "key"));
            case "delete_memory":
                return (Loc.Tool_Forgetting, ExtractJsonField(argsJson, "key"));
            case "recall_memory":
                return (Loc.Tool_Recalling, ExtractJsonField(argsJson, "key"));

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

