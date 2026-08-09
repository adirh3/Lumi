using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
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

    private static IReadOnlyList<Button> Rows(Window window, string itemsControlName)
    {
        var host = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == itemsControlName);
        return [.. host.GetVisualDescendants().OfType<Button>()];
    }

    [Fact]
    public async Task ChatRowsRenderAndTheirCommandsResolveToTheListViewModel()
    {
        await Run(
            shell => shell.ChatList.Apply(
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
            shell => new MobileDrawerView { DataContext = shell },
            (window, shell) =>
            {
                var rows = Rows(window, "DrawerChatGroups");
                Assert.Equal(3, rows.Count);

                // The command binding is the exact thing that used to throw. A bound, executable
                // command proves the cast through the vm: prefix resolved.
                Assert.Same(shell.ChatList.OpenChatCommand, rows[0].Command);
                Assert.NotNull(rows[0].CommandParameter);
                Assert.True(
                    rows.All(row => row.Transitions is null || row.Transitions.Count == 0),
                    "chat selection must snap; a background transition leaves the previous row lit briefly");

                var busy = window.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "BusyIndicator" && border.IsEffectivelyVisible);
                Assert.Equal(6, busy.Bounds.Width);
                Assert.Equal(6, busy.Bounds.Height);
                Assert.True(StrataTheme.Animation.LifecycleOpacityPulse.GetIsActive(busy));
                Assert.Equal(1, StrataTheme.Animation.LifecycleOpacityPulse.GetFromOpacity(busy));
                Assert.Equal(0.3, StrataTheme.Animation.LifecycleOpacityPulse.GetToOpacity(busy));
                Assert.Contains(
                    rows[0].GetVisualDescendants().OfType<Border>(),
                    border => border.Classes.Contains("project-badge") && border.IsEffectivelyVisible);
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
    public async Task LibraryRowsRenderAndBindTheEditCommand()
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
                var row = Assert.Single(Rows(window, "LibraryEntries"));
                Assert.Same(shell.Library.BeginEditCommand, row.Command);
            });
    }

    [Fact]
    public async Task TranscriptTurnsAndNestedToolCallsRender()
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
                var transcript = window.GetVisualDescendants().OfType<ItemsControl>()
                    .Single(c => c.Name == "Transcript");

                Assert.Single(shell.Chat.Turns);
                Assert.Equal(8, shell.Chat.Turns[0].Items.Count);
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

                var planButton = window.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == "PlanButton");
                Assert.True(planButton.IsEffectivelyVisible);
                shell.Chat.TogglePlanCommand.Execute(null);
                var planSheet = window.GetVisualDescendants().OfType<StrataTheme.Controls.StrataBottomSheet>()
                    .Single(sheet => sheet.Name == "PlanSheet");
                Assert.True(planSheet.IsOpen);
                Assert.IsType<ScrollViewer>(planSheet.Content);
            });
    }
}
