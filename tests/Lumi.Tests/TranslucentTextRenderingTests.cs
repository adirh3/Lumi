using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Appearance;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class TranslucentTextRenderingTests
{
    private static readonly TextOptions Grayscale = new() { TextRenderingMode = TextRenderingMode.Antialias };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsAndChatIslandsKeepPlatformTextRendering(bool detached)
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            Window window = detached ? new ChatWindow() : new MainWindow();
            window.Show();
            try
            {
                var workspace = window.FindControl<ChatWorkspaceView>(detached ? "DetachedChatView" : "ChatContentGrid")!;
                Assert.Equal(default(TextOptions), EffectiveTextOptions(window));
                Assert.Equal(default(TextOptions), EffectiveTextOptions(workspace));
                Assert.Equal(default(TextOptions), EffectiveTextOptions(workspace.FindControl<Border>("ChatIsland")!));
                Assert.Equal(default(TextOptions), EffectiveTextOptions(workspace.FindControl<Border>("WorkspacePanel")!));

                WindowMaterials.Apply(window, UserSettings.WindowMaterialSolid);
                Assert.Equal(default(TextOptions), EffectiveTextOptions(window));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatChromeUsesGrayscaleWithoutChangingTranscriptTextRendering()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var chat = new Chat
            {
                Title = "Scoped text rendering",
                Messages =
                [
                    new ChatMessage { Role = "user", Content = "A normal message." },
                    new ChatMessage { Role = "assistant", Content = "The transcript keeps its default rendering." },
                ],
            };
            var data = new AppData
            {
                Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false },
                Chats = [chat],
            };
            using var viewModel = new ChatViewModel(new DataStore(data), TestCopilot.Shared);
            await viewModel.LoadChatAsync(chat);
            var view = new ChatView { DataContext = viewModel, ShowInternalTitle = true };
            var window = new Window { Content = view, Width = 1100, Height = 820 };
            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();

                Assert.Equal(Grayscale, EffectiveTextOptions(view.FindControl<TextBlock>("ChatTitleText")!));
                var composer = view.FindControl<StrataChatComposer>("Composer")!;
                var input = composer.GetVisualDescendants().OfType<TextBox>()
                    .Single(control => control.Name == "PART_Input");
                Assert.Equal(Grayscale, EffectiveTextOptions(input));
                Assert.Equal(default(TextOptions), EffectiveTextOptions(view));

                var transcript = view.FindControl<TranscriptItemsControl>("Transcript")!;
                Assert.NotEmpty(transcript.GetVisualDescendants().OfType<TranscriptTurnControl>());
                Assert.Equal(default(TextOptions), EffectiveTextOptions(transcript));
                Assert.All(transcript.GetVisualDescendants(),
                    visual => Assert.Equal(default(TextOptions), EffectiveTextOptions(visual)));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OpaqueWindowsKeepPlatformTextRendering()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            Loc.Load("en");
            foreach (var window in new Window[] { new Window(), new StartupWindow(), new CrashReportWindow() })
            {
                window.Show();
                try
                {
                    Assert.Equal(default(TextOptions), TextOptions.GetTextOptions(window));
                }
                finally
                {
                    window.Close();
                }
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NativePopupHostKeepsPlatformTextRendering()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            var styles = Assert.IsType<Styles>(AvaloniaXamlLoader.Load(
                new Uri("avares://StrataTheme/Controls/PopupRoot.axaml")));
            var setter = styles.OfType<Style>()
                .SelectMany(style => style.Setters.OfType<Setter>())
                .Single(value => value.Property == TemplatedControl.TemplateProperty);
            var template = Assert.IsAssignableFrom<IControlTemplate>(setter.Value);
            var root = Assert.IsType<Panel>(template.Build(new ContentControl())!.Result);

            Assert.Equal(default(TextOptions), TextOptions.GetTextOptions(root));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OpaqueOverlayPopupKeepsPlatformTextRendering()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            var target = new Button { Content = "Open" };
            var window = new Window { Width = 600, Height = 400 };

            var label = new TextBlock { Text = "Ordinary popup text" };
            var popup = new Popup
            {
                PlacementTarget = target,
                ShouldUseOverlayLayer = true,
                Child = label,
            };
            window.Content = new Panel { Children = { target, popup } };

            window.Show();
            try
            {
                window.UpdateLayout();
                popup.IsOpen = true;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var root = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(label));
                root.UpdateLayout();

                Assert.True(popup.IsUsingOverlayLayer);
                Assert.Same(window, root);

                Assert.Equal(default(TextOptions), EffectiveTextOptions(label));
            }
            finally
            {
                popup.IsOpen = false;
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("model", "PART_ModelPickerPopup")]
    [InlineData("skills", "PART_Popup")]
    [InlineData("connectors", "PART_Popup")]
    [InlineData("autocomplete", "PART_AutoCompletePopup")]
    [InlineData("add", "PART_AddPopup")]
    public async Task GlassPopupContentOptsInWithoutChangingItsTrigger(string kind, string popupName)
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(() =>
        {
            Control control = kind switch
            {
                "model" => new StrataModelPicker(),
                "skills" => new StrataComposerPicker(),
                "connectors" => new StrataConnectorPicker(),
                _ => new StrataChatComposer(),
            };
            var window = new Window { Content = control, Width = 800, Height = 600 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var popup = control.GetVisualDescendants().OfType<Popup>()
                    .Single(value => value.Name == popupName);
                Assert.Equal(Grayscale, TextOptions.GetTextOptions(Assert.IsAssignableFrom<Control>(popup.Child)));
                Assert.Equal(default(TextOptions), EffectiveTextOptions(control));
                Assert.Equal(default(TextOptions), EffectiveTextOptions(window));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static TextOptions EffectiveTextOptions(Visual visual)
    {
        var options = TextOptions.GetTextOptions(visual);
        foreach (var ancestor in visual.GetVisualAncestors())
            options = options.MergeWith(TextOptions.GetTextOptions(ancestor));
        return options;
    }
}
