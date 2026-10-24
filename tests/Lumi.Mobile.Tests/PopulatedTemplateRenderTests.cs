using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// Renders every item template in the app against real, non-empty data.
///
/// These exist because the whole app crashed on first contact with content and nothing caught it.
/// A DataTemplate's body is deferred: it is not built until an item exists, so a shell rendered with
/// empty lists — which is what every other view test did — never executes it. The first chat row the
/// phone tried to paint threw
/// <c>ArgumentException: Unable to resolve type vm:ChatListViewModel</c> and took the process down,
/// because <c>{Binding $parent[UserControl].((vm:T)DataContext).Cmd}</c> was being evaluated as a
/// runtime reflection binding, and the <c>vm:</c> xmlns prefix is not resolvable inside nested
/// deferred template content. The same binding shape sat in the discovery list and the library list,
/// so three of the app's four surfaces were fatal on first use.
///
/// Compiled bindings (see AvaloniaUseCompiledBindingsByDefault in Lumi.Mobile.csproj) fix the cause.
/// These tests pin the symptom: each list must actually materialize its rows.
/// </summary>
[Collection("Headless mobile UI")]
public sealed class PopulatedTemplateRenderTests
{
    /// <summary>
    /// Populates a real shell view model, renders one view against it, and forces the deferred
    /// template content to build inside the try block — so a XAML failure fails the test instead of
    /// crashing the render loop the way it crashed the app.
    /// </summary>
    private static async Task Run(
        Action<MobileShellViewModel> arrange,
        Func<MobileShellViewModel, Control> buildContent,
        Action<Window, MobileShellViewModel> assert)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;
            try
            {
                shell = new MobileShellViewModel(store: session.NewStore(), post: action => action());
                arrange(shell);

                window = new Window { Width = 412, Height = 892, Content = buildContent(shell) };
                window.Show();
                window.InvalidateMeasure();
                Dispatcher.UIThread.RunJobs();

                assert(window, shell);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window?.Close();
                if (shell is not null)
                    await shell.DisposeAsync();
            }
        }, CancellationToken.None);

        failure?.Throw();
    }

    private static void AssertChatCardSurface(Control control, string partName)
    {
        var surface = control.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Name == partName);
        var chat = control.GetVisualAncestors().OfType<ChatDetailView>().First();
        Assert.Equal(1, surface.BorderThickness.Left);
        Assert.True(surface.CornerRadius.TopLeft >= 12);
        Assert.NotEqual(
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(chat.Background).Color,
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(surface.Background).Color);
    }

    private static IReadOnlyList<Button> Rows(Window window, string itemsControlName)
    {
        var host = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == itemsControlName);
        return [.. host.GetVisualDescendants().OfType<Button>()];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatRowsRenderAndTheirCommandsResolveToTheListViewModel(bool search)
    {
        await Run(
            shell => (search ? shell.SearchChatList : shell.ChatList).Apply(
            [
                new RemoteChatGroup
                {
                    Label = "Today",
                    Chats =
                    [
                        new RemoteChat
                        {
                            Id = Guid.NewGuid(),
                            Title = "Pinned chat",
                            Preview = "last thing said",
                            ProjectName = "Lumi",
                            AgentName = "Coding Lumi",
                            AgentGlyph = "\u26A1",
                            IsPinned = true,
                            IsRunning = true,
                            HasUnreadMessages = true
                        },
                        new RemoteChat { Id = Guid.NewGuid(), Title = "Plain chat" }
                    ]
                },
                new RemoteChatGroup
                {
                    Label = "Yesterday",
                    Chats = [new RemoteChat { Id = Guid.NewGuid(), Title = "Older chat" }]
                }
            ]),
            shell => search
                ? new MobileSearchView { DataContext = shell }
                : new MobileDrawerView { DataContext = shell },
            (window, shell) =>
            {
                var list = search ? shell.SearchChatList : shell.ChatList;
                var rows = Rows(window, search ? "SearchResults" : "DrawerChatGroups")
                    .Where(row => ReferenceEquals(row.Command, list.OpenChatCommand)).ToList();
                Assert.Equal(3, rows.Count);

                // The command binding is the exact thing that used to throw. A bound, executable
                // command proves the cast through the vm: prefix resolved.
                Assert.Same(list.OpenChatCommand, rows[0].Command);
                Assert.NotNull(rows[0].CommandParameter);
                if (!search)
                    Assert.True(
                        rows.All(row => row.Transitions is null || row.Transitions.Count == 0),
                        "chat selection must snap; a background transition leaves the previous row lit briefly");

                Assert.Contains("running", rows[0].Classes);
                T Part<T>(string cssClass) where T : Control =>
                    Assert.Single(rows[0].GetVisualDescendants().OfType<T>(), part => part.Classes.Contains(cssClass));
                var title = Part<TextBlock>("conversation-title");
                Assert.Equal("Pinned chat", title.Text);
                // The desktop's row marks: one dot before the title (pulsing while Lumi works, steady
                // while a reply waits), a ring while background work runs, and no time: the date
                // groups already say when.
                var dot = Part<Border>("chat-row-dot");
                var background = Part<Border>("chat-row-background");
                Assert.Equal("BackgroundActivityIndicator", background.Name);
                Assert.False(background.IsEffectivelyVisible);
                Assert.DoesNotContain(rows[0].GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text is { Length: > 0 } value && (value.EndsWith('m') || value.EndsWith('h') || value == "now")
                            && text.IsEffectivelyVisible && !ReferenceEquals(text, title));
                Assert.True(dot.TranslatePoint(new Point(dot.Bounds.Width, 0), rows[0])!.Value.X
                            <= title.TranslatePoint(default, rows[0])!.Value.X);
                // Search keeps a second line (where the chat lives and its last reply) to recognise a
                // match by; the chat list is one line.
                TextBlock? preview = search ? Part<TextBlock>("conversation-preview") : null;
                Border? project = search ? Part<Border>("project-chip") : null;
                TextBlock? working = search ? Part<TextBlock>("conversation-working") : null;
                if (!search)
                {
                    Assert.DoesNotContain(rows[0].GetVisualDescendants().OfType<Control>(),
                        part => part.Classes.Contains("conversation-preview") || part.Classes.Contains("project-chip")
                                || part.Classes.Contains("conversation-working"));
                    Assert.InRange(rows[0].Bounds.Height, 48, 52);
                    var titleCentre = title.TranslatePoint(new Point(0, title.Bounds.Height / 2), rows[0])!.Value.Y;
                    var dotCentre = dot.TranslatePoint(new Point(0, dot.Bounds.Height / 2), rows[0])!.Value.Y;
                    Assert.InRange(dotCentre - titleCentre, -1, 1);
                    // The pinned chat carries the desktop's quiet pin at the row's end.
                    var pin = Assert.Single(rows[0].GetVisualDescendants().OfType<PathIcon>(),
                        icon => icon.Name == "ChatRowPin");
                    Assert.True(pin.IsEffectivelyVisible);
                    Assert.False(Assert.Single(rows[1].GetVisualDescendants().OfType<PathIcon>(),
                        icon => icon.Name == "ChatRowPin").IsEffectivelyVisible);
                }

                // Working: the dot pulses as the desktop's running marker does; search also says so in
                // place of the last reply.
                Assert.True(StrataTheme.Animation.LifecycleOpacityPulse.GetIsActive(dot));
                Assert.True(dot.IsEffectivelyVisible);
                if (search)
                {
                    Assert.True(working!.IsEffectivelyVisible);
                    Assert.False(preview!.IsEffectivelyVisible);
                    Assert.True(project!.IsEffectivelyVisible);
                }
                Assert.Equal(6, dot.Bounds.Width);
                Assert.Equal(6, dot.Bounds.Height);
                Assert.Equal(0, dot.BorderThickness.Left);
                Assert.Equal(1, StrataTheme.Animation.LifecycleOpacityPulse.GetFromOpacity(dot));
                Assert.Equal(0.3, StrataTheme.Animation.LifecycleOpacityPulse.GetToOpacity(dot));
                Assert.Equal(TimeSpan.FromSeconds(1.2), StrataTheme.Animation.LifecycleOpacityPulse.GetDuration(dot));
                Assert.Equal(Avalonia.Animation.PlaybackDirection.Alternate,
                    StrataTheme.Animation.LifecycleOpacityPulse.GetPlaybackDirection(dot));
                Assert.Equal(StrataTheme.Animation.LifecycleOpacityPulseEasing.Linear,
                    StrataTheme.Animation.LifecycleOpacityPulse.GetEasing(dot));

                // Rows without a mark share one text axis; a marked title steps past its dot, as on the desktop.
                var plainTitle = Assert.Single(rows[1].GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Classes.Contains("conversation-title"));
                var olderTitle = Assert.Single(rows[2].GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Classes.Contains("conversation-title"));
                Assert.Equal(plainTitle.TranslatePoint(default, rows[1])!.Value.X,
                    olderTitle.TranslatePoint(default, rows[2])!.Value.X, precision: 1);
                Assert.InRange(title.TranslatePoint(default, rows[0])!.Value.X
                               - plainTitle.TranslatePoint(default, rows[1])!.Value.X, 12, 18);
                var vm = Assert.IsType<ChatListItemViewModel>(rows[0].DataContext);
                Assert.Equal(vm.AccessibilityName, Avalonia.Automation.AutomationProperties.GetName(rows[0]));

                // Read while still working: the dot keeps pulsing.
                vm.HasUnreadMessages = false;
                Dispatcher.UIThread.RunJobs();
                Assert.True(StrataTheme.Animation.LifecycleOpacityPulse.GetIsActive(dot));
                Assert.True(dot.IsEffectivelyVisible);

                // Done with a reply waiting: the dot stays, steady.
                vm.HasUnreadMessages = true;
                vm.IsSessionActive = false;
                vm.IsRunning = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(StrataTheme.Animation.LifecycleOpacityPulse.GetIsActive(dot));
                Assert.True(dot.IsEffectivelyVisible);
                if (search)
                {
                    Assert.False(working!.IsEffectivelyVisible);
                    Assert.True(preview!.IsEffectivelyVisible);
                    Assert.Equal("last thing said", preview.Text);
                }

                // Background work after the reply: the desktop's ring at the row's end.
                vm.IsSessionActive = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(background.IsEffectivelyVisible);
                Assert.Equal(8, background.Bounds.Width);
                Assert.Equal(8, background.Bounds.Height);
                vm.IsSessionActive = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(background.IsEffectivelyVisible);

                // Read and idle: no mark at all.
                vm.HasUnreadMessages = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(dot.IsEffectivelyVisible);
            });
    }

    [Fact]
    public async Task DiscoveredHostRowsRenderAndBindTheChooseCommand()
    {
        await Run(
            shell => shell.Connect.Hosts.Add(new DiscoveredHostViewModel
            {
                HostName = "LIGHTO-DESKTOP",
                UserName = "Adir",
                BaseUrl = "http://192.168.1.20:47653"
            }),
            shell => new ConnectView { DataContext = shell.Connect },
            (window, shell) =>
            {
                var row = Assert.Single(Rows(window, "HostList"));
                Assert.Same(shell.Connect.ChooseHostCommand, row.Command);
            });
    }

    [Fact]
    public async Task LibraryRowsRenderAndBindTheOpenCommand()
    {
        await Run(
            shell => shell.Library.Apply(new RemoteLibrary
            {
                Projects =
                [
                    new RemoteProject { Id = Guid.NewGuid(), Name = "Lumi", Instructions = "Be great", ChatCount = 4 }
                ]
            }),
            shell => new LibraryView { DataContext = shell.Library },
            (window, shell) =>
            {
                var row = Assert.Single(Rows(window, "LibraryEntries"),
                    button => ReferenceEquals(button.Command, shell.Library.OpenEntryCommand));
                Assert.Same(shell.Library.OpenEntryCommand, row.Command);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedQaWorkDisclosureContainsExpandedPreamblesAndKeepsTheFinalOutside(bool dark)
    {
        await Run(
            shell =>
            {
                shell.IsPaired = true;
                shell.Chat.Reset(Guid.NewGuid(), "Work timeline");
                shell.Chat.ApplyTranscript(WorkTimelineTests.RecordedQaTranscript(shell.Chat.ChatId));
            },
            shell => new ChatDetailView { DataContext = shell },
            (window, shell) =>
            {
                window.RequestedThemeVariant = dark
                    ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                Thread.Sleep(250);
                Dispatcher.UIThread.RunJobs();
                const string finalId = "43a1dc625cd643d689fd7646ac2d1ce2";
                var preambleIds = new[] { "88b852c448e7447d854bb511ed8c9529", "7341167d7b874750aec8e2b7bc1093ec" };
                var turn = shell.Chat.Turns[0];
                var work = Assert.IsType<WorkSummaryItemViewModel>(turn.DisplayItems[1]);
                // The desktop's turn summary: one quiet line that opens the folded work in place.
                var summary = Assert.Single(window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataTurnSummary>(),
                    control => ReferenceEquals(control.DataContext, work));
                Assert.Equal("Worked for 58s · 2 actions", summary.Label);
                Assert.Contains("touch", summary.Classes);
                var label = Assert.Single(summary.GetVisualDescendants().OfType<TextBlock>(), text => text.Name == "PART_Label");
                // The meta rung: quieter than the answer, still phone-readable.
                Assert.Equal(14.5, label.FontSize);
                Assert.Equal(new[] { finalId }, VisibleAnswers());

                work.ToggleCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(preambleIds.Append(finalId), VisibleAnswers());
                Assert.True(work.IsExpanded);
                Assert.True(summary.IsExpanded);
                var workItems = Assert.Single(window.GetVisualDescendants().OfType<ItemsControl>(),
                    control => control.Name == "WorkItems");
                Assert.Same(work.Items, workItems.ItemsSource);
                Assert.Equal(preambleIds,
                    workItems.GetVisualDescendants().OfType<StrataTheme.Controls.StrataMarkdown>()
                        .Select(control => ((AssistantItemViewModel)control.DataContext!).Id));
                Assert.Equal(3, turn.DisplayItems.Count);
                Assert.Equal(finalId, turn.DisplayItems[^1].Id);
                Assert.Contains(window.GetVisualDescendants().OfType<Button>(),
                    control => control.DataContext is ActivitySummaryItemViewModel && control.IsEffectivelyVisible);
                foreach (var activity in window.GetVisualDescendants().OfType<Button>()
                             .Where(control => control.DataContext is ActivitySummaryItemViewModel))
                    AssertChatCardSurface(activity, "PART_Surface");

                work.ToggleCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.False(summary.IsExpanded);
                Assert.Equal(new[] { finalId }, VisibleAnswers());

                string[] VisibleAnswers() => window.GetVisualDescendants()
                    .OfType<StrataTheme.Controls.StrataMarkdown>()
                    .Where(control => control.IsEffectivelyVisible && control.DataContext is AssistantItemViewModel)
                    .Select(control => ((AssistantItemViewModel)control.DataContext!).Id)
                    .ToArray();
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedAnswerPartsRenderOutsideCollapsedProgress(bool dark)
    {
        await Run(
            shell =>
            {
                shell.IsPaired = true;
                shell.Chat.Reset(Guid.NewGuid(), "Multipart answer");
                shell.Chat.ApplyTranscript(new RemoteTranscript
                {
                    ChatId = shell.Chat.ChatId,
                    Revision = 1,
                    Turns =
                    [
                        new()
                        {
                            Id = "turn", FinalAnswerId = "answer-2", WorkDurationMs = 3_000,
                            Items =
                            [
                                new() { Id = "user", Kind = "user", Text = "Check both layouts." },
                                new() { Id = "progress", Kind = "assistant", Text = "Checking the layouts.", IsProgress = true },
                                new() { Id = "activity", Kind = "activity", Status = "Completed", ActionCount = 2 },
                                new() { Id = "answer-1", Kind = "assistant", Text = "The phone layout passes.", IsProgress = false },
                                new() { Id = "answer-2", Kind = "assistant", Text = "The unfolded layout passes too.", IsProgress = false }
                            ]
                        }
                    ]
                });
            },
            shell => new ChatDetailView { DataContext = shell },
            (window, shell) =>
            {
                window.RequestedThemeVariant = dark
                    ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                Dispatcher.UIThread.RunJobs();
                var turn = Assert.Single(shell.Chat.Turns);
                var work = Assert.Single(turn.DisplayItems.OfType<WorkSummaryItemViewModel>());
                var summary = Assert.Single(window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataTurnSummary>());
                Assert.False(summary.IsExpanded);
                Assert.Equal(new[] { "answer-1", "answer-2" }, VisibleAnswers());

                work.ToggleCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.True(summary.IsExpanded);
                Assert.Equal(new[] { "progress", "answer-1", "answer-2" }, VisibleAnswers());
                Assert.Equal(new[] { "progress", "activity" }, work.Items.Select(item => item.Id));

                work.ToggleCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(new[] { "answer-1", "answer-2" }, VisibleAnswers());

                string[] VisibleAnswers() => window.GetVisualDescendants()
                    .OfType<StrataTheme.Controls.StrataMarkdown>()
                    .Where(control => control.IsEffectivelyVisible && control.DataContext is AssistantItemViewModel)
                    .Select(control => ((AssistantItemViewModel)control.DataContext!).Id)
                    .ToArray();
            });
    }

    /// <summary>
    /// The phone's transcript wears the desktop's: a bubble keeps the width its words need instead of
    /// leaving room for a hover toolbar no phone shows, "You · time" sits over it, the files a turn made
    /// sit inside the answer that closes it ahead of its sources and the footer that names the model
    /// and time, finished work folds behind one line, and live work is a glass chip with the Strata line.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscriptWearsTheDesktopsGlassAtTouchSize(bool dark)
    {
        var asked = new DateTimeOffset(2026, 10, 18, 6, 41, 0, TimeSpan.Zero);
        var answered = asked.AddMinutes(3);
        static string Time(DateTimeOffset value) =>
            value.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        await Run(
            shell =>
            {
                shell.IsPaired = true;
                shell.Chat.ApplyCatalogs(new RemoteSettings
                {
                    PreferredModel = "claude-opus-5.5",
                    AvailableModels = ["claude-opus-5.5"],
                    ModelDisplayNames = ["claude-opus-5.5=Claude Opus 5.5"]
                });
                shell.Chat.Reset(Guid.NewGuid(), "Glass");
                shell.Chat.ApplyTranscript(new RemoteTranscript
                {
                    ChatId = shell.Chat.ChatId, Title = "Glass", Revision = 1,
                    Turns =
                    [
                        new RemoteTranscriptTurn
                        {
                            Id = "t1", FinalAnswerId = "a1", WorkDurationMs = 12_000,
                            Items =
                            [
                                new RemoteTranscriptItem
                                {
                                    Id = "u1", Kind = RemoteProtocol.ItemKinds.User, Timestamp = asked,
                                    Text = "The composer jumps to the top when I come back from the file picker with the keyboard open. Fix it and run the tests.",
                                    Attachments = [new RemoteAttachment { FileName = "before.png", Extension = ".png" }]
                                },
                                new RemoteTranscriptItem
                                {
                                    Id = "w1", Kind = RemoteProtocol.ItemKinds.Activity, ActivityId = "w1", Status = "Completed",
                                    ActionCount = 3, DurationMs = 12_000, Icon = "edit", FileChangeCount = 1,
                                    FileChanges = [new RemoteFileChange { Path = "src/A.cs", FileName = "A.cs", Operation = "Modified", LinesAdded = 14, LinesRemoved = 5 }]
                                },
                                new RemoteTranscriptItem
                                {
                                    Id = "a1", Kind = RemoteProtocol.ItemKinds.Assistant, Model = "claude-opus-5.5", Timestamp = answered,
                                    Text = "Fixed.", Sources = [new RemoteSource { Title = "Docs", Url = "https://learn.example.com/insets" }]
                                },
                                new RemoteTranscriptItem
                                {
                                    Id = "f1", Kind = RemoteProtocol.ItemKinds.File,
                                    Attachments = [new RemoteAttachment { MessageId = Guid.NewGuid(), FileName = "after.png", Extension = ".png" }]
                                }
                            ]
                        },
                        new RemoteTranscriptTurn
                        {
                            Id = "t2",
                            Items =
                            [
                                new RemoteTranscriptItem { Id = "u2", Kind = RemoteProtocol.ItemKinds.User, Timestamp = answered, Text = "Run them on my Fold." },
                                new RemoteTranscriptItem
                                {
                                    Id = "w2", Kind = RemoteProtocol.ItemKinds.Activity, ActivityId = "w2", Status = "InProgress",
                                    ActionCount = 2, Label = "Running device tests...", Icon = "terminal"
                                }
                            ]
                        }
                    ]
                });
            },
            shell => new ChatDetailView { DataContext = shell },
            (window, shell) =>
            {
                window.RequestedThemeVariant = dark
                    ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                Thread.Sleep(250);
                Dispatcher.UIThread.RunJobs();
                double Top(Visual visual) => visual.TranslatePoint(default, window)!.Value.Y;
                double Bottom(Visual visual) => visual.TranslatePoint(new Point(0, visual.Bounds.Height), window)!.Value.Y;
                T Named<T>(Visual root, string name) where T : Control =>
                    root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name && control.IsEffectivelyVisible);

                // The user's bubble: "You · time" over it, the attachment inside it, and the width its words
                // need: no column kept for the desktop's hover toolbar.
                var user = window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataChatMessage>()
                    .Single(message => message.DataContext is UserTurnItemViewModel { Id: "u1" });
                Assert.Equal("You", user.Author);
                Assert.Equal(Time(asked), user.Timestamp);
                Assert.False(user.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_ActionLayer").IsVisible);
                var bubble = user.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_Bubble");
                Assert.True(bubble.Bounds.Width >= 300, $"The bubble is {bubble.Bounds.Width:0} dp wide in a 412 dp window.");
                Assert.Contains(bubble.GetVisualDescendants().OfType<StrataTheme.Controls.StrataFileAttachment>(),
                    file => file.Classes.Contains("transcript-file"));

                // The closing answer carries the turn's files, then its sources, then the footer.
                Assert.DoesNotContain(shell.Chat.Turns[0].DisplayItems, item => item is FileItemViewModel);
                var answer = window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataMarkdown>()
                    .Single(markdown => markdown.DataContext is AssistantItemViewModel { Id: "a1" });
                var files = Named<ItemsControl>(window, "AnswerFiles");
                var sources = Named<Button>(window, "SourcesChip");
                var footer = Named<TextBlock>(window, "AnswerFooter");
                Assert.Single(files.GetVisualDescendants().OfType<StrataTheme.Controls.StrataFileAttachment>());
                Assert.True(Top(files) >= Bottom(answer) - 0.5);
                Assert.True(Top(sources) >= Bottom(files) - 0.5);
                Assert.True(Top(footer) >= Bottom(sources) - 0.5);
                Assert.Equal($"Claude Opus 5.5 · {Time(answered)}", footer.Text);
                AssertChatCardSurface(sources, "PART_Surface");

                // Finished work folds behind one line; live work is a glass chip in a full touch band,
                // in the accent, with the Strata line under it.
                var fold = Assert.Single(window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataTurnSummary>());
                Assert.Equal("Worked for 12s · 3 actions", fold.Label);
                var live = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "ActivityChip" && button.DataContext is ActivitySummaryItemViewModel { ActivityId: "w2" });
                Assert.Contains("live", live.Classes);
                Assert.Equal(48, live.Bounds.Height, 1);
                var chip = live.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_Surface");
                Assert.Equal(38, chip.Bounds.Height, 1);
                AssertChatCardSurface(live, "PART_Surface");
                Assert.Contains(live.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Running device tests...");
                Assert.Contains(live.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "2 actions");
                Assert.True(((Panel)live.Parent!).Children.OfType<Border>()
                    .Single(border => border.Classes.Contains("single-step-line")).IsEffectivelyVisible);

                // Opened, the fold shows the finished chip with the lines it changed, and no Strata line.
                fold.IsExpanded = true;
                Thread.Sleep(100);
                Dispatcher.UIThread.RunJobs();
                var done = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "ActivityChip" && button.DataContext is ActivitySummaryItemViewModel { ActivityId: "w1" });
                Assert.DoesNotContain("live", done.Classes);
                Assert.Contains(done.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "+14" && text.IsEffectivelyVisible);
                Assert.Contains(done.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "\u22125" && text.IsEffectivelyVisible);
                Assert.Contains(done.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Worked for 12s");
                Assert.False(((Panel)done.Parent!).Children.OfType<Border>()
                    .Single(border => border.Classes.Contains("single-step-line")).IsVisible);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscriptTurnsAndNestedToolCallsRender(bool dark)
    {
        await Run(
            shell =>
            {
                shell.IsPaired = true;
                shell.Chat.Reset(Guid.NewGuid(), "Rendered");
                shell.Chat.ApplyTranscript(new RemoteTranscript
                {
                    ChatId = shell.Chat.ChatId,
                    Title = "Rendered",
                    Revision = 1,
                    Turns =
                    [
                        new RemoteTranscriptTurn
                        {
                            Id = "t1",
                            Items =
                            [
                                new RemoteTranscriptItem { Id = "u1", Kind = RemoteProtocol.ItemKinds.User, Text = "hi" },
                                new RemoteTranscriptItem { Id = "r1", Kind = RemoteProtocol.ItemKinds.Reasoning, Text = "thinking" },
                                new RemoteTranscriptItem
                                {
                                    Id = "g1",
                                    Kind = RemoteProtocol.ItemKinds.ToolGroup,
                                    Label = "Ran 2 tools",
                                    Tools =
                                    [
                                        new RemoteToolCall { Id = "c1", Name = "powershell", DisplayName = "Ran a command", Status = "Completed" },
                                        new RemoteToolCall { Id = "c2", Name = "view", DisplayName = "Read a file", Status = "InProgress" }
                                    ]
                                },
                                new RemoteTranscriptItem
                                {
                                    Id = "term1",
                                    Kind = RemoteProtocol.ItemKinds.Terminal,
                                    Label = "powershell",
                                    Tools =
                                    [
                                        new RemoteToolCall
                                        {
                                            Id = "term-call",
                                            Name = "powershell",
                                            Input = "Get-Date",
                                            Output = "Tuesday",
                                            Status = "Completed"
                                        }
                                    ]
                                },
                                new RemoteTranscriptItem
                                {
                                    Id = "activity1",
                                    Kind = RemoteProtocol.ItemKinds.Activity,
                                    ActivityId = "activity1",
                                    Status = "Completed",
                                    ActionCount = 4,
                                    DurationMs = 4_800,
                                    FileChanges =
                                    [
                                        new RemoteFileChange
                                        {
                                            Path = "src/Auth.cs",
                                            FileName = "Auth.cs",
                                            Operation = "Modified",
                                            LinesAdded = 8,
                                            LinesRemoved = 2
                                        }
                                    ]
                                },
                                new RemoteTranscriptItem
                                {
                                    Id = "q1",
                                    Kind = RemoteProtocol.ItemKinds.Question,
                                    Question = new RemoteQuestion
                                    {
                                        QuestionId = "question-1",
                                        Text = "Pick one",
                                        Options = ["A", "B"],
                                        AllowFreeText = true
                                    }
                                },
                                new RemoteTranscriptItem
                                {
                                    Id = "file1",
                                    Kind = RemoteProtocol.ItemKinds.File,
                                    Attachments =
                                    [
                                        new RemoteAttachment
                                        {
                                            Path = @"C:\Temp\report.pdf",
                                            FileName = "report.pdf",
                                            Extension = "pdf"
                                        }
                                    ]
                                },
                                new RemoteTranscriptItem
                                {
                                    Id = "err1",
                                    Kind = RemoteProtocol.ItemKinds.Error,
                                    Text = "Something failed"
                                },
                                new RemoteTranscriptItem
                                {
                                    Id = "a1",
                                    Kind = RemoteProtocol.ItemKinds.Assistant,
                                    Text = "**done**",
                                    Sources =
                                    [
                                        new RemoteSource
                                        {
                                            Title = "Lumi docs",
                                            Snippet = "Verified source",
                                            Url = "https://example.test"
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                });

                shell.Chat.ApplyStatus(new RemoteChatStatus
                {
                    ChatId = shell.Chat.ChatId,
                    PlanContent = "# Plan\n\n- verify every mobile row"
                });
            },
            shell => new ChatDetailView { DataContext = shell },
            (window, shell) =>
            {
                window.RequestedThemeVariant = dark
                    ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                Thread.Sleep(250);
                Dispatcher.UIThread.RunJobs();
                var transcript = window.GetVisualDescendants().OfType<ItemsControl>()
                    .Single(c => c.Name == "Transcript");

                Assert.Single(shell.Chat.Turns);
                Assert.Equal(9, shell.Chat.Turns[0].Items.Count);
                Assert.Contains(shell.Chat.Turns[0].Items, item => item is ActivitySummaryItemViewModel);
                Assert.Contains(shell.Chat.Turns[0].Items, item => item is TerminalItemViewModel);
                Assert.Contains(shell.Chat.Turns[0].Items, item => item is QuestionItemViewModel);
                Assert.Contains(shell.Chat.Turns[0].Items, item => item is FileItemViewModel);
                Assert.Contains(shell.Chat.Turns[0].Items, item => item is ErrorItemViewModel);
                Assert.Contains(
                    shell.Chat.Turns[0].Items,
                    item => item is AssistantItemViewModel { HasSources: true });

                // The nested tool template is a second level of deferred content — the level that
                // could not resolve types at runtime. Reaching realized children proves it built.
                Assert.NotEmpty(transcript.GetVisualDescendants().OfType<Control>());
                Assert.NotEmpty(window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataTerminalPreview>());
                Assert.NotEmpty(window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataQuestionCard>());
                Assert.NotEmpty(window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataFileAttachment>());
                var deliverable = Assert.Single(window.GetVisualDescendants()
                    .OfType<StrataTheme.Controls.StrataFileAttachment>(), file => file.Classes.Contains("transcript-file"));
                Assert.False(deliverable.IsCompact);
                Assert.False(deliverable.IsRemovable);
                // The desktop's file chip at touch size: a full target, sized to its name rather than
                // stretched into a row, with the file's type drawn as an icon rather than an emoji.
                Assert.True(deliverable.Bounds.Height >= 48);
                Assert.True(deliverable.Bounds.Width < 320);
                Assert.NotNull(deliverable.IconImage);
                AssertChatCardSurface(deliverable, "PART_Root");
                var fileName = Assert.Single(deliverable.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Name == "PART_FileName");
                Assert.Equal(14.5, fileName.FontSize);
                var fileOrigin = deliverable.TranslatePoint(default, window)!.Value;
                var answer = window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataMarkdown>()
                    .Single(markdown => markdown.DataContext is AssistantItemViewModel);
                Assert.True(fileOrigin.Y >= answer.TranslatePoint(new Point(0, answer.Bounds.Height), window)!.Value.Y);
                foreach (var card in window.GetVisualDescendants().OfType<Control>()
                             .Where(control => control is StrataTheme.Controls.StrataAiToolCall
                                 or StrataTheme.Controls.StrataTerminalPreview))
                    AssertChatCardSurface(card, "PART_Root");
                foreach (var reasoning in window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataThink>())
                    AssertChatCardSurface(reasoning, "PART_Pill");
                var sourcesChip = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                    button => button.Name == "SourcesChip" && button.IsEffectivelyVisible);
                AssertChatCardSurface(sourcesChip, "PART_Surface");

                var sourceAnswer = Assert.IsType<AssistantItemViewModel>(
                    shell.Chat.Turns[0].Items.Single(item =>
                        item is AssistantItemViewModel { HasSources: true }));
                sourceAnswer.OpenSourcesCommand.Execute(null);
                var sourcesSheet = window.GetVisualDescendants()
                    .OfType<StrataTheme.Controls.StrataBottomSheet>()
                    .Single(sheet => sheet.Name == "SourcesSheet");
                Assert.True(sourcesSheet.IsOpen);
                Assert.Single(sourceAnswer.Sources);
                Assert.True(shell.Chat.DismissTopmostSheet());

                shell.Chat.OpenRunSettingsSheetCommand.Execute(null);
                window.InvalidateMeasure();
                Dispatcher.UIThread.RunJobs();
                var planButton = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "PlanButton");
                Assert.True(planButton.IsEffectivelyVisible);
                planButton.Command!.Execute(planButton.CommandParameter);
                Assert.False(shell.Chat.IsRunSettingsSheetOpen);
                var planSheet = window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataBottomSheet>()
                    .Single(sheet => sheet.Name == "PlanSheet");
                Assert.True(planSheet.IsOpen);
                Assert.IsType<ScrollViewer>(planSheet.Content);
            });
    }
}
