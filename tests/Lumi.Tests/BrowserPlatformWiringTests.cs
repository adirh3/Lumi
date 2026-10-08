using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class BrowserPlatformWiringTests
{
    [Fact]
    public async Task WindowHostsKeepScaledContentAndCloseIndependently()
    {
        var copilot = TestCopilot.Shared;
        using var session = HeadlessTestSession.Start();
        var verified = false;
        Exception? bodyFailure = null;
        void ExerciseWindows()
        {
            Loc.Load("en");
            var store = new DataStore(new AppData
            {
                Settings = new UserSettings
                {
                    IsOnboarded = true,
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                    ShowAnimations = false,
                    ShowAmbientPresence = false,
                }
            });
            using var mainVm = new MainViewModel(
                store, copilot, new UpdateService(),
                startBackgroundJobs: false, initializeCopilotOnStartup: false);
            using var detachedChat = new ChatViewModel(store, copilot);
            using var detachedVm = new ChatWindowViewModel(detachedChat);
            // A configured shell gives queued nav initialization real, measurable controls.
            var main = new MainWindow
            {
                DataContext = mainVm,
                IsPrimaryWindow = false,
                Width = 1000,
                Height = 720,
            };
            var detached = new ChatWindow(store, detachedVm) { Width = 1000, Height = 720 };
            main.Show();
            detached.Show();
            try
            {
                FlushLayout(main);
                FlushLayout(detached);
                var mainLayer = AssertWindowHost(main);
                var detachedLayer = AssertWindowHost(detached);

                main.Close();
                main.Content = null;
                FlushLayout(detached);

                Assert.False(main.IsVisible);
                Assert.True(detached.IsVisible);
                Assert.Same(detachedLayer, AssertWindowHost(detached));
#if !WINDOWS
                Assert.NotSame(mainLayer, detachedLayer);
                Assert.Same(detached, TopLevel.GetTopLevel(detachedLayer!));
#endif
            }
            finally
            {
                main.Close();
                detached.Close();
                main.Content = null;
                detached.Content = null;
            }
            verified = true;
        }

        try
        {
            await session.Dispatch(() =>
            {
                try { ExerciseWindows(); }
                catch (Exception ex) { bodyFailure = ex; }
            }, CancellationToken.None);
        }
        catch (Exception resetFailure) when (bodyFailure is not null)
        {
            throw new AggregateException("Window fixture failed before dispatcher teardown.", bodyFailure, resetFailure);
        }

        if (bodyFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(bodyFailure).Throw();

        Assert.True(verified, "Headless window-host validation did not complete.");
    }

    [Fact]
    public async Task BrowserSettingsAreVisibleWhileVoiceAndHotkeysStayPlatformGated()
    {
        using var session = HeadlessTestSession.Start();
        var verified = false;
        ValueTask browserDisposal = default;
        try
        {
            await session.Dispatch(() =>
            {
                Loc.Load("en");
                var store = new DataStore(new AppData
                {
                    Settings = new UserSettings { AutoSaveChats = false, EnableMemoryAutoSave = false }
                });
                var browser = new BrowserService();
                try
                {
                    using var settings = new SettingsViewModel(store, TestCopilot.Shared, browser, new UpdateService());
                    using var chat = new ChatViewModel(store, TestCopilot.Shared);
                    var view = new SettingsView { DataContext = settings };
                    var window = new Window { Width = 1000, Height = 800, Content = view };
                    settings.SelectedPageIndex = 6;
                    window.Show();
                    try
                    {
                        FlushLayout(window);

                        var available = NativeBrowserLogic.IsEmbeddedBrowserAvailable;
                        Assert.Equal(available, settings.IsEmbeddedBrowserAvailable);
                        Assert.Equal(OperatingSystem.IsWindows(), settings.IsGlobalHotkeyAvailable);
                        Assert.Equal(OperatingSystem.IsWindows(), chat.IsVoiceAvailable);
                        var privacy = view.FindControl<StackPanel>("PagePrivacy");
                        Assert.NotNull(privacy);
                        var browserGroup = Assert.Single(privacy.GetLogicalDescendants()
                            .OfType<StrataSettingGroup>(), group => group.Header?.ToString() == "Browser");
                        Assert.Equal(available, browserGroup.IsEffectivelyVisible);
                        // SettingContent is rendered by a template, not exposed as a logical child.
                        var buttons = browserGroup.GetVisualDescendants().OfType<Button>().ToArray();
                        if (available)
                        {
                            var import = Assert.Single(buttons, button =>
                                ReferenceEquals(button.Command, settings.ImportBrowserCookiesAgainCommand));
                            var reset = Assert.Single(buttons, button =>
                                ReferenceEquals(button.Command, settings.ResetBrowserCookiesCommand));
                            foreach (var button in new[] { import, reset })
                            {
                                Assert.True(button.IsEffectivelyVisible);
                                Assert.Same(window, TopLevel.GetTopLevel(button));
                                Assert.True(button.Bounds.Width > 0);
                                Assert.True(button.Bounds.Height > 0);
                            }
                        }
                        else
                        {
                            Assert.DoesNotContain(buttons, static button => button.IsEffectivelyVisible);
                        }
                    }
                    finally
                    {
                        window.Close();
                    }
                }
                finally
                {
                    // Start disposal on the UI thread, but await it outside the Action dispatcher.
                    browserDisposal = browser.DisposeAsync();
                }
                verified = true;
            }, CancellationToken.None);
        }
        finally
        {
            await browserDisposal;
        }

        Assert.True(verified, "Headless browser-settings validation did not complete.");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("he")]
    public async Task CookieImportHintsMatchTheHostWithoutChangingWindowsWording(string language)
    {
        using var session = HeadlessTestSession.Start();
        string?[]? browserHints = null;
        string?[]? settingsHints = null;
        string? linuxSessionHint = null;
        await session.Dispatch(() =>
        {
            Loc.Load(language);
            try
            {
                linuxSessionHint = Loc.Browser_LinuxTabSessionHint;
                var browser = new BrowserView();
                var onboarding = browser.FindControl<Border>("CookieOnboardingOverlay");
                browserHints = onboarding?.GetLogicalDescendants().OfType<TextBlock>()
                    .Select(hint => hint.Text).ToArray();

                var settings = new SettingsView();
                var dialog = settings.FindControl<StrataDialog>("CookieImportDialog");
                var dialogContent = dialog?.DialogContent as Control;
                settingsHints = dialogContent?.GetLogicalDescendants().OfType<TextBlock>()
                    .Select(hint => hint.Text).ToArray();
            }
            finally
            {
                Loc.Load("en");
            }
        }, CancellationToken.None);

        Assert.NotNull(browserHints);
        Assert.NotNull(settingsHints);
        Assert.NotNull(linuxSessionHint);
        AssertCookieHints(browserHints, linuxSessionHint);
        AssertCookieHints(settingsHints, linuxSessionHint);
    }

    private static Canvas? AssertWindowHost(Window window)
    {
        var scaledContent = window.FindControl<LayoutTransformControl>("UiScaleHost");
        Assert.NotNull(scaledContent);
        Assert.True(scaledContent.IsEffectivelyVisible);
        Assert.Same(window, TopLevel.GetTopLevel(scaledContent));
        Assert.True(scaledContent.Bounds.Width > 0);
        Assert.True(scaledContent.Bounds.Height > 0);
#if WINDOWS
        Assert.Same(scaledContent, window.Content);
        Assert.DoesNotContain(window.GetVisualDescendants(),
            visual => visual is Canvas { Name: "NativeWebViewLayer" });
        return null;
#else
        var root = Assert.IsType<Grid>(window.Content);
        Assert.Equal("NativeWebViewHostRoot", root.Name);
        Assert.Equal(2, root.Children.Count);
        Assert.Same(scaledContent, root.Children[0]);
        var layer = Assert.IsType<Canvas>(root.Children[1]);
        Assert.Equal("NativeWebViewLayer", layer.Name);
        Assert.Same(window, TopLevel.GetTopLevel(layer));
        Assert.True(layer.IsVisible);
        Assert.True(layer.ClipToBounds);
        Assert.True(layer.IsHitTestVisible);
        Assert.Null(layer.Background);
        Assert.Empty(layer.Children);
        Assert.Equal(root.Bounds.Size, layer.Bounds.Size);
        return layer;
#endif
    }

    private static void AssertCookieHints(IEnumerable<string?> hints, string linuxSessionHint)
    {
        var text = hints.ToArray();
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("• The selected browser will be briefly closed", text);
            Assert.Contains("• Windows may ask for your password to authorize", text);
        }
        else
        {
            Assert.Contains("• Close the selected browser first if its cookie database is locked", text);
            Assert.Contains("• Your system may ask to unlock its credential store", text);
            Assert.DoesNotContain("• The selected browser will be briefly closed", text);
            Assert.DoesNotContain("• Windows may ask for your password to authorize", text);
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains(linuxSessionHint, text);
            Assert.DoesNotContain("• Your cookies will be copied into Lumi's browser", text);
        }
        else
        {
            Assert.DoesNotContain(linuxSessionHint, text);
            Assert.Contains("• Your cookies will be copied into Lumi's browser", text);
        }
    }

    private static void FlushLayout(Window window)
    {
        // Flush measure/arrange without draining self-posting shell initialization jobs.
        window.UpdateLayout();
    }
}
