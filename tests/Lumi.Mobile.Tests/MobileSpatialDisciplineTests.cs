using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Layout;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;
using Xunit.Abstractions;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class MobileSpatialDisciplineTests
{
    private readonly ITestOutputHelper _output;

    public MobileSpatialDisciplineTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupUsesTheRealLumiImageAndFitsA360By780Phone(bool lightTheme)
    {
        await Run(
            360,
            780,
            shell => shell.Connect.Hosts.Add(new DiscoveredHostViewModel
            {
                HostName = "LIGHTO-DESKTOP",
                UserName = "Adir",
                BaseUrl = "http://192.168.1.20:47653"
            }),
            (shell, window, _) =>
            {
                window.RequestedThemeVariant = lightTheme ? ThemeVariant.Light : ThemeVariant.Dark;
                Pump(window);

                var connect = Required<ConnectView>(window, "ConnectPage");
                var image = Required<Image>(connect, "ConnectOrb");
                var manualAddressBox = Required<TextBox>(connect, "ManualAddressBox");
                var manualConnectButton = Required<Button>(connect, "ManualConnectButton");
                var hostRow = Required<ItemsControl>(connect, "HostList")
                    .GetVisualDescendants().OfType<Button>().Single();
                var assetUri = new Uri("avares://Lumi.Mobile/Assets/lumi-icon.png");

                Assert.True(AssetLoader.Exists(assetUri));
                Assert.NotNull(image.Source);
                Assert.True(image.IsEffectivelyVisible);
                Assert.True(
                    image.Bounds.Width > 0 && image.Bounds.Height > 0,
                    $"The Lumi image rendered at {image.Bounds.Width:0.#}x{image.Bounds.Height:0.#}.");
                Assert.Empty(connect.GetVisualDescendants().OfType<StrataOrb>());

                var findAxes = new[]
                {
                    Left(Required<Control>(connect, "SearchButton"), window),
                    Left(hostRow, window),
                    Left(manualAddressBox, window)
                };
                AssertSharedAxis(findAxes, expected: 24, tolerance: 1);

                var content = Required<Control>(connect, "ConnectContent");
                AssertInsideViewport(manualConnectButton, window);
                Assert.Equal(24, Left(content, window), 1);
                hostRow.BringIntoView();
                Pump(window);
                AssertInsideViewport(hostRow, window);
                AssertInteractiveMinimum(connect, 48);
                Assert.True(manualConnectButton.IsDefault);

                shell.Connect.ManualAddress = "100.85.249.111:65534";
                var goArgs = new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Enter
                };
                manualAddressBox.RaiseEvent(goArgs);
                Pump(window);
                Assert.True(goArgs.Handled);
                Assert.Equal(ConnectStep.Connecting, shell.Connect.Step);

                _output.WriteLine(
                    "{0} setup: brand StrataOrb 72x72 -> Image {1:0.#}x{2:0.#}; " +
                    "content axes 24/24/41 -> {3}; final action bottom 622 -> {4:0.#}.",
                    lightTheme ? "Light" : "Dark",
                    image.Bounds.Width,
                    image.Bounds.Height,
                    string.Join("/", findAxes.Select(value => value.ToString("0.#"))),
                    Bottom(Required<Control>(connect, "ManualConnectButton"), window));

                shell.Connect.TargetHostName = "LIGHTO-DESKTOP";
                shell.Connect.PairingCode = "123456";
                shell.Connect.Step = ConnectStep.EnterCode;
                Pump(window);

                var pairingCodeBox = Required<TextBox>(connect, "PairingCodeBox");
                Assert.True(pairingCodeBox.IsEffectivelyVisible);
                Assert.Equal(TextInputContentType.Number, TextInputOptions.GetContentType(pairingCodeBox));
                Assert.Equal(TextInputReturnKeyType.Done, TextInputOptions.GetReturnKeyType(pairingCodeBox));
                Assert.False(TextInputOptions.GetShowSuggestions(pairingCodeBox));
                Assert.True(pairingCodeBox.IsFocused);
                Assert.True(Required<Control>(connect, "PairButton").IsEffectivelyVisible);
                Assert.True(Required<Control>(connect, "PairBackButton").IsEffectivelyVisible);
                AssertInsideViewport(Required<Control>(connect, "ConnectContent"), window);
                AssertInteractiveMinimum(connect, 48);
            },
            store => new MobileShellViewModel(
                client: new LumiRemoteClient(
                    "test-device",
                    "Test phone",
                    new BlockingHandler(),
                    requestDeadline: TimeSpan.FromSeconds(10),
                    uploadDeadline: TimeSpan.FromSeconds(10),
                    routeVerifier: new TrustedRouteVerifier()),
                store: store,
                post: action => action()));
    }

    [Fact]
    public async Task PrimaryPagesShareTheSixteenDpPhoneGutter()
    {
        await Run(
            360,
            780,
            shell =>
            {
                PairAndOpenChat(shell);
                RemoteChatGroup[] groups =
                [
                    new RemoteChatGroup
                    {
                        Label = "Today",
                        Chats = [new RemoteChat { Id = Guid.NewGuid(), Title = "Aligned chat" }]
                    }
                ];
                shell.ChatList.Apply(groups);
                shell.SearchChatList.Apply(groups);
                shell.Library.Apply(new RemoteLibrary
                {
                    Projects =
                    [
                        new RemoteProject
                        {
                            Id = Guid.NewGuid(),
                            Name = "Lumi",
                            Instructions = "Build Lumi",
                            ChatCount = 4
                        }
                    ]
                });
            },
            (shell, window, _) =>
            {
                shell.Page = MobilePage.Search;
                Pump(window);
                var search = Required<MobileSearchView>(window, "SearchPage");
                var searchRow = Required<ItemsControl>(search, "SearchResults")
                    .GetVisualDescendants().OfType<Button>().Single();
                AssertSharedAxis(
                [
                    Left(Required<Control>(search, "SearchHeaderLayout"), window),
                    Left(Required<Control>(search, "SearchResultsContent"), window),
                    Left(searchRow, window)
                ], expected: 16, tolerance: 1);

                shell.Page = MobilePage.Library;
                Pump(window);
                var library = Required<LibraryView>(window, "LibraryPage");
                var libraryRow = Required<ItemsControl>(library, "LibraryEntries")
                    .GetVisualDescendants().OfType<Button>()
                    .Single(button => ReferenceEquals(button.Command, shell.Library.BeginEditCommand));
                AssertSharedAxis(
                [
                    Left(Required<Control>(library, "LibraryHeaderLayout"), window),
                    Left(Required<Control>(library, "SectionPicker"), window),
                    Left(Required<Control>(library, "LibrarySearchBox"), window),
                    Left(Required<Control>(library, "LibraryListContent"), window),
                    Left(libraryRow, window)
                ], expected: 16, tolerance: 1);

                shell.Page = MobilePage.Settings;
                Pump(window);
                var settings = Required<MobileSettingsView>(window, "SettingsPage");
                AssertSharedAxis(
                [
                    Left(Required<Control>(settings, "SettingsHeaderLayout"), window),
                    Left(Required<Control>(settings, "SettingsContent"), window),
                    Left(Required<Control>(settings, "SettingsConnectionCard"), window)
                ], expected: 16, tolerance: 1);

                _output.WriteLine(
                    "Search, Library and Settings share a 16dp page gutter.");
            });
    }

    [Fact]
    public async Task DrawerUsesConsistentGuttersAndAlignedNavigationIcons()
    {
        await Run(
            1100,
            900,
            shell =>
            {
                PairAndOpenChat(shell);
                shell.IsSidebarCollapsed = false;
                shell.Projects.Add(new ProjectPickViewModel { Id = Guid.NewGuid(), Name = "Lumi" });
                shell.ChatList.Apply(
                [
                    new RemoteChatGroup
                    {
                        Label = "Today",
                        Chats =
                        [
                            .. Enumerable.Range(0, 121).Select(index => new RemoteChat
                            {
                                Id = Guid.NewGuid(),
                                Title = $"Chat {index + 1}",
                                ProjectName = index == 0 ? "Lumi" : null
                            })
                        ]
                    }
                ]);
            },
            (_, window, _) =>
            {
                var drawer = Required<MobileDrawerView>(window, "DrawerContent");
                var projectButton = Required<Button>(drawer, "DrawerProjectButton");
                var chatButton = Required<ItemsControl>(drawer, "DrawerChatGroups")
                    .GetVisualDescendants().OfType<Button>().First();
                var rows = new[]
                {
                    Required<Button>(drawer, "DrawerLibraryButton"),
                    chatButton,
                    Required<Button>(drawer, "DrawerLoadMoreButton")
                };
                Assert.All(rows, row => Assert.Equal(12, Left(row, drawer), 1));
                Assert.Equal(16, Left(projectButton, drawer), 1);
                Assert.Equal("All projects", Required<TextBlock>(drawer, "DrawerProjectLabel").Text);
                var settings = Required<Button>(drawer, "DrawerAccountButton");
                Assert.Equal(48, settings.Bounds.Width);
                Assert.Equal(drawer.Bounds.Width - 12,
                    settings.TranslatePoint(new Point(settings.Bounds.Width, 0), drawer)!.Value.X, 1);
                var iconTiles = new[]
                {
                    Required<Button>(drawer, "DrawerLibraryButton")
                }.Select(row => row.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Classes.Contains("navigation-icon-tile")));
                AssertSharedAxis(iconTiles.Select(tile => Left(tile, drawer)).ToArray(), expected: 24, tolerance: 1);
                var chatTitle = chatButton.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Classes.Contains("conversation-title"));
                Assert.Equal(24, Left(chatTitle, drawer), 1);
                Assert.Single(drawer.GetVisualDescendants().OfType<ScrollViewer>());
                AssertInteractiveMinimum(drawer, 48);

                _output.WriteLine("Navigation surfaces use a 12dp gutter and a shared 24dp icon/section axis.");
            });
    }

    [Fact]
    public async Task PhoneAndUnfoldedFoldReserveRoomForComposerElevation()
    {
        await Run(
            360,
            780,
            PairAndOpenChat,
            (shell, window, shellView) =>
            {
                var composer = Required<StrataChatComposer>(window, "Composer");
                var transcript = Required<Control>(window, "ChatTranscriptSideInset");
                var composerHost = Required<Border>(window, "PART_ComposerHost");
                var scrollContent = Required<Border>(window, "PART_ScrollContent");
                var composerRoot = Required<Border>(composer, "PART_Root");
                var input = Required<TextBox>(composer, "PART_Input");

                Assert.Equal(new Thickness(12, 10, 12, 12), composerHost.Padding);
                Assert.Equal(new Thickness(16, 12, 16, 16), scrollContent.Padding);
                Assert.Equal(336, composer.Bounds.Width, 1);
                Assert.Equal(328, transcript.Bounds.Width, 1);
                Assert.Equal(760, composer.MaxWidth, 1);
                Assert.Equal(1, composerRoot.BorderThickness.Left, 1);
                Assert.True(composerRoot.BoxShadow.Count > 0);
                var elevation = composerRoot.BoxShadow;
                AssertInteractiveMinimum(Required<ChatDetailView>(window, "ChatSurface"), 48);

                var pointerPoint = composerRoot.TranslatePoint(
                    new Point(composerRoot.Bounds.Width / 2, composerRoot.Bounds.Height / 2),
                    window);
                Assert.NotNull(pointerPoint);
                window.MouseMove(pointerPoint!.Value);
                Thread.Sleep(200);
                Pump(window);
                Assert.True(composerRoot.IsPointerOver);
                Assert.Equal(elevation, composerRoot.BoxShadow);
                var neutralBorderColor =
                    Assert.IsAssignableFrom<ISolidColorBrush>(composerRoot.BorderBrush).Color;

                Assert.True(input.Focus());
                Thread.Sleep(200);
                Pump(window);
                Assert.True(input.IsFocused);
                Assert.Equal(elevation, composerRoot.BoxShadow);
                var focusedBorderColor =
                    Assert.IsAssignableFrom<ISolidColorBrush>(composerRoot.BorderBrush).Color;
                Assert.Equal(neutralBorderColor, focusedBorderColor);
                var focusAccentBar = input.GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Name == "FocusAccentBar");
                Assert.True(
                    focusAccentBar.IsVisible,
                    "the input underline is the intended accent; only the outer composer border should stay neutral");
                var focusedUnderlineOpacity = focusAccentBar.GetBaseValue(Visual.OpacityProperty);
                Assert.True(
                    focusedUnderlineOpacity.HasValue && focusedUnderlineOpacity.Value > 0.9,
                    $"the focused underline target stayed at {focusedUnderlineOpacity.GetValueOrDefault():0.##}");

                _output.WriteLine(
                    "Phone chat: composer {0:0.#}; transcript {1:0.#}; " +
                    "elevation gutter {2}; transcript padding {3}.",
                    composer.Bounds.Width,
                    transcript.Bounds.Width,
                    composerHost.Padding,
                    scrollContent.Padding);

                shell.IsSidebarCollapsed = false;
                shellView.Posture = FoldPosture.BookVerticalHinge;
                shellView.HingeSize = 24;
                shellView.HingePosition = 430;
                window.Width = 884;
                window.Height = 908;
                Pump(window);

                composer = Required<StrataChatComposer>(window, "Composer");
                transcript = Required<Control>(window, "ChatTranscriptSideInset");
                Assert.Equal(430, Required<Control>(window, "ConversationFrame").Bounds.Width, 1);
                Assert.Equal(430, Required<Control>(window, "ChatSurface").Bounds.Width, 1);
                Assert.Equal(406, composer.Bounds.Width, 1);
                Assert.Equal(398, transcript.Bounds.Width, 1);

                _output.WriteLine(
                    "Unfolded fold: composer {0:0.#}; transcript {1:0.#}.",
                    composer.Bounds.Width,
                    transcript.Bounds.Width);
            });
    }

    [Fact]
    public async Task AuxiliaryActionsAndComposerChipRemovalHaveFullTouchTargets()
    {
        await Run(
            412,
            892,
            PairAndOpenChat,
            (shell, window, _) =>
            {
                var earlierActivityButton = Required<Button>(window, "EarlierActivityButton");
                Assert.IsAssignableFrom<Control>(earlierActivityButton.Parent).IsVisible = true;
                earlierActivityButton.IsVisible = true;
                Required<Control>(window, "NewerActivityControls").IsVisible = true;
                Required<Control>(window, "NewerActivityButton").IsVisible = true;
                Required<Control>(window, "ReturnToLatestButton").IsVisible = true;

                shell.Chat.Attachments.Add(new PendingAttachment("layout.png", @"C:\layout.png"));
                Required<Control>(window, "PendingAttachments").IsVisible = true;

                var composer = Required<StrataChatComposer>(window, "Composer");
                composer.AgentName = "Coding Lumi";
                composer.ProjectName = "Lumi";
                composer.SkillItems = new[] { new StrataComposerChip("Review") };
                Pump(window);

                foreach (var name in new[]
                         {
                             "EarlierActivityButton",
                             "NewerActivityButton",
                             "ReturnToLatestButton"
                         })
                {
                    var button = Required<Button>(window, name);
                    Assert.True(
                        button.Bounds.Height >= 48,
                        $"{name} was only {button.Bounds.Height:0.#}dp high");
                }

                var removeButton = Assert.Single(
                    window.GetVisualDescendants()
                        .OfType<Button>(),
                    button =>
                            button.Classes.Contains("chip-remove") &&
                            button.DataContext is PendingAttachment &&
                            button.IsVisible &&
                            button.IsEffectivelyVisible);

                Assert.True(
                    removeButton.Bounds.Width is >= 48 and <= 52 &&
                    removeButton.Bounds.Height is >= 48 and <= 52,
                    $"Attachment removal target was {removeButton.Bounds.Width:0.#}×" +
                    $"{removeButton.Bounds.Height:0.#}dp");

                foreach (var name in new[] { "PART_AgentRemoveButton", "PART_ProjectRemoveButton" })
                {
                    var button = Required<Button>(composer, name);
                    Assert.True(
                        button.Bounds.Width is >= 48 and <= 52 &&
                        button.Bounds.Height is >= 48 and <= 52,
                        $"{name} was {button.Bounds.Width:0.#}×{button.Bounds.Height:0.#}dp");
                }

                var skillRemoveButton = Assert.Single(
                    composer.GetVisualDescendants().OfType<Button>(),
                    button =>
                        button.Classes.Contains("chip-remove") &&
                        button.DataContext is not PendingAttachment);
                Assert.True(
                    skillRemoveButton.Bounds.Width is >= 48 and <= 52 &&
                    skillRemoveButton.Bounds.Height is >= 48 and <= 52,
                    $"Skill removal target was {skillRemoveButton.Bounds.Width:0.#}×" +
                    $"{skillRemoveButton.Bounds.Height:0.#}dp");
            });
    }

    private static async Task Run(
        double width,
        double height,
        Action<MobileShellViewModel> arrange,
        Action<MobileShellViewModel, Window, MobileShellView> assert,
        Func<MobileSettingsStore, MobileShellViewModel>? createShell = null)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;

        await session.Dispatch(async () =>
        {
            MobileShellViewModel? shell = null;
            Window? window = null;

            try
            {
                var store = session.NewStore();
                shell = createShell?.Invoke(store)
                    ?? new MobileShellViewModel(store: store, post: action => action());
                arrange(shell);

                var shellView = new MobileShellView { DataContext = shell };
                window = new Window
                {
                    Width = width,
                    Height = height,
                    Content = shellView
                };
                window.Show();
                Pump(window);

                assert(shell, window, shellView);
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

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking test transport should only end through cancellation.");
        }
    }

    private sealed class TrustedRouteVerifier : IRemoteRouteVerifier
    {
        public bool IsTrustedTailscaleRoute(System.Net.IPAddress targetAddress) => true;
    }

    private static void PairAndOpenChat(MobileShellViewModel shell)
    {
        shell.HostName = "LIGHTO-DESKTOP";
        shell.IsPaired = true;
        shell.Chat.Reset(Guid.NewGuid(), "Spatial discipline");
    }

    private static T Required<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static double Left(Control control, Visual relativeTo) =>
        control.TranslatePoint(default, relativeTo)?.X
        ?? throw new InvalidOperationException($"{control.Name ?? control.GetType().Name} is not attached.");

    private static double Top(Control control, Visual relativeTo) =>
        control.TranslatePoint(default, relativeTo)?.Y
        ?? throw new InvalidOperationException($"{control.Name ?? control.GetType().Name} is not attached.");

    private static double Bottom(Control control, Visual relativeTo) =>
        control.TranslatePoint(new Point(0, control.Bounds.Height), relativeTo)?.Y
        ?? throw new InvalidOperationException($"{control.Name ?? control.GetType().Name} is not attached.");

    private static double ContentLeft(Button button, Visual relativeTo)
    {
        if (button.Content is Control content &&
            content.TranslatePoint(default, relativeTo) is { } contentOrigin)
        {
            return contentOrigin.X;
        }

        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(control => ReferenceEquals(control.Content, button.Content))
            ?? button.GetVisualDescendants().OfType<ContentPresenter>().First();
        return Left(presenter, relativeTo);
    }

    private static void AssertSharedAxis(
        IReadOnlyCollection<double> axes,
        double expected,
        double tolerance)
    {
        Assert.NotEmpty(axes);
        Assert.True(
            axes.Max() - axes.Min() <= 4,
            $"Axes were {string.Join(", ", axes.Select(value => value.ToString("0.#")))}.");
        Assert.All(axes, axis => Assert.Equal(expected, axis, tolerance));
    }

    private static void AssertInsideViewport(Control control, Window window)
    {
        var top = Top(control, window);
        var bottom = Bottom(control, window);
        Assert.True(top >= -1, $"{control.Name} started above the viewport at {top:0.#}.");
        Assert.True(
            bottom <= window.ClientSize.Height + 1,
            $"{control.Name} ended at {bottom:0.#} on a {window.ClientSize.Height:0.#}dp viewport.");
    }

    private static void AssertInteractiveMinimum(Control root, double floor)
    {
        var tooSmall = root.GetVisualDescendants().OfType<Control>()
            .Where(control => control is Button or TextBox or ToggleSwitch or ListBoxItem or Slider)
            .Where(control => control is not RepeatButton)
            .Where(control => control.IsEffectivelyVisible)
            .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .Where(control => control.Bounds.Width < floor || control.Bounds.Height < floor)
            .Select(control =>
                $"{control.Name ?? control.Classes.FirstOrDefault() ?? control.GetType().Name} " +
                $"{control.Bounds.Width:0.#}x{control.Bounds.Height:0.#}")
            .Distinct()
            .ToList();

        Assert.Empty(tooSmall);
    }

    private static void Pump(Window window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
    }
}
