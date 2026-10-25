using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Localization;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class MobileLocalizationTests
{
    [Theory]
    [InlineData("en", "Copy")]
    [InlineData("he", "העתקה")]
    public async Task PlanMarkdownUsesTheUiLanguageWithoutChangingItsContent(string language, string copy)
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            using var cultures = new CultureScope();
            var store = new MemorySettingsStore();
            store.Settings.Language = language;
            await using var shell = new MobileShellViewModel(store: store, post: action => action());
            const string content = "# Original plan\n\n```text\nKeep this code unchanged.\n```";
            var chatId = Guid.NewGuid();
            shell.IsPaired = true;
            shell.Chat.Reset(chatId, "Original chat");
            shell.Chat.ApplyTranscript(new RemoteTranscript { ChatId = chatId });
            shell.Chat.ApplyStatus(new RemoteChatStatus { ChatId = chatId, PlanContent = content });
            shell.Chat.IsPlanOpen = true;
            var view = new ChatDetailView { DataContext = shell };
            var window = new Window { Width = 412, Height = 892, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var markdown = Assert.IsType<StrataMarkdown>(view.FindControl<StrataMarkdown>("PlanMarkdown"));
                Assert.Equal(language, markdown.InteractionLanguage);
                Assert.Equal(content, markdown.Markdown);
                Assert.Equal(content, shell.Chat.PlanContent);
                Assert.Contains(markdown.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == copy);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("en", "en", "Settings", FlowDirection.LeftToRight)]
    [InlineData("he", "he", "הגדרות", FlowDirection.RightToLeft)]
    [InlineData("he-IL", "he", "הגדרות", FlowDirection.RightToLeft)]
    [InlineData("unsupported", "en", "Settings", FlowDirection.LeftToRight)]
    public void StartupCultureSelectsResourcesAndBackgroundThreadDefaults(
        string requested, string expectedLanguage, string settings, FlowDirection direction)
    {
        using var cultures = new CultureScope();
        ChatSessionStrings.Load(requested);

        Assert.Equal(expectedLanguage, ChatSessionStrings.Language);
        Assert.Equal(expectedLanguage, CultureInfo.CurrentCulture.Name);
        Assert.Equal(expectedLanguage, CultureInfo.CurrentUICulture.Name);
        Assert.Equal(expectedLanguage, CultureInfo.DefaultThreadCurrentCulture?.Name);
        Assert.Equal(expectedLanguage, CultureInfo.DefaultThreadCurrentUICulture?.Name);
        Assert.Equal(direction, ChatSessionStrings.UiFlowDirection);
        Assert.Equal(settings, ChatSessionStrings.Get("Settings"));
        Assert.Equal(settings, new StrExtension("Settings").ProvideValue(null!));
        Assert.Equal("missing-mobile-key", ChatSessionStrings.Get("missing-mobile-key"));
    }

    [Fact]
    public void EmbeddedCatalogsHaveMatchingKeysAndValidFormatArguments()
    {
        var manager = new ResourceManager(
            "Lumi.Mobile.Localization.ChatSessionStrings", typeof(ChatSessionStrings).Assembly);
        var english = ReadCatalog(manager, CultureInfo.InvariantCulture);
        var hebrew = ReadCatalog(manager, CultureInfo.GetCultureInfo("he"));

        Assert.Contains("Settings", english.Keys);
        Assert.Contains("MobileTruncationNotice", english.Keys);
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), hebrew.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, value) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), key);
            Assert.False(string.IsNullOrWhiteSpace(hebrew[key]), key);
            Assert.Equal(FormatArguments(value), FormatArguments(hebrew[key]));
            foreach (var localized in new[] { value, hebrew[key] })
            {
                var format = CompositeFormat.Parse(localized);
                var arguments = Enumerable.Repeat<object>(42, format.MinimumArgumentCount).ToArray();
                _ = string.Format(CultureInfo.InvariantCulture, localized, arguments);
            }
        }
    }

    [Theory]
    [InlineData("en", "Update and discard", FlowDirection.LeftToRight)]
    [InlineData("he", "עדכון וויתור על שינויים", FlowDirection.RightToLeft)]
    public async Task WebAppUpdateConfirmationUsesTheUiLanguageAndKeepsTheDraft(
        string language, string expectedAction, FlowDirection direction)
    {
        using var session = HeadlessMobileSession.Start();
        await session.Dispatch(async () =>
        {
            using var cultures = new CultureScope();
            var previousHost = MobilePlatformServices.HostEnvironment;
            MobilePlatformServices.HostEnvironment = new UpdateableWebHost();
            var store = new MemorySettingsStore();
            store.Settings.Language = language;
            await using var shell = new MobileShellViewModel(store: store, post: action => action());
            shell.IsPaired = true;
            shell.Chat.PromptText = "Keep this original draft";
            shell.WebAppUpdateState = WebAppUpdateState.Ready;
            shell.ApplyWebAppUpdateCommand.Execute(null);
            var view = new WebAppUpdateBannerView
            {
                DataContext = shell, FlowDirection = ChatSessionStrings.UiFlowDirection
            };
            var window = new Window { Width = 320, Height = 600, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var confirm = view.FindControl<Button>("ConfirmWebAppUpdateButton")!;
                Assert.True(confirm.IsEffectivelyVisible);
                Assert.True(confirm.IsEffectivelyEnabled);
                Assert.Equal(expectedAction, Assert.IsType<TextBlock>(confirm.Content).Text);
                Assert.Equal(direction, confirm.FlowDirection);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == ChatSessionStrings.Get("UpdateDiscardHelp"));
                Assert.True(confirm.Bounds.Width >= 48 && confirm.Bounds.Height >= 48);
                Assert.True(confirm.TranslatePoint(new Avalonia.Point(confirm.Bounds.Width, 0), view)!.Value.X
                            <= view.Bounds.Width);
                shell.CancelWebAppUpdateCommand.Execute(null);
                Assert.False(shell.IsWebAppUpdateConfirmationOpen);
                Assert.Equal("Keep this original draft", shell.Chat.PromptText);
                Assert.False(shell.CanApplyWebAppUpdate);
            }
            finally
            {
                window.Close();
                MobilePlatformServices.HostEnvironment = previousHost;
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LanguageChoiceIsSavedWithoutChangingTheLiveDraftAndAppliesOnRestart()
    {
        using var cultures = new CultureScope();
        var store = new MemorySettingsStore();
        await using (var shell = new MobileShellViewModel(store: store, post: action => action()))
        {
            shell.Chat.PromptText = "An unsent draft — טיוטה";
            shell.SelectLanguageCommand.Execute("he");

            Assert.Equal("he", store.Settings.Language);
            Assert.True(shell.IsHebrewLanguage);
            Assert.True(shell.LanguageRestartRequired);
            Assert.Equal("en", ChatSessionStrings.Language);
            Assert.Equal("An unsent draft — טיוטה", shell.Chat.PromptText);
            Assert.Equal("System", store.Settings.Theme);
        }

        await using var reopened = new MobileShellViewModel(store: store, post: action => action());
        Assert.Equal("he", ChatSessionStrings.Language);
        Assert.False(reopened.LanguageRestartRequired);
        Assert.Equal("חיבור הטלפון", reopened.Connect.PairActionText);
        Assert.Contains("כישורים", reopened.Library.SectionNames);
        Assert.Equal("גבוה", MobileChatViewModel.EffortDisplay("high"));
        Assert.Equal("ברירת מחדל", MobileChatViewModel.ContextDisplay("Default"));
        Assert.Equal("provider-defined", MobileChatViewModel.ContextDisplay("provider-defined"));
    }

    [Fact]
    public void GeneratedTruncationSuffixIsTranslatedOnlyAtThePresentationBoundary()
    {
        using var cultures = new CultureScope();
        var source = "User/model content stays exact." + RemoteProtocol.MobileTruncationMarker;
        ChatSessionStrings.Load("en");
        Assert.Equal(source, ChatSessionStrings.LocalizeMobileTruncation(source));

        ChatSessionStrings.Load("he");
        var expected = "User/model content stays exact.\n\n" + ChatSessionStrings.Get("MobileTruncationNotice");
        Assert.Equal(expected, ChatSessionStrings.LocalizeMobileTruncation(source));
        Assert.Null(ChatSessionStrings.LocalizeMobileTruncation(null));
        Assert.Equal(source + " not a generated suffix",
            ChatSessionStrings.LocalizeMobileTruncation(source + " not a generated suffix"));

        var item = new RemoteTranscriptItem
        {
            Id = "localized-display", Kind = RemoteProtocol.ItemKinds.Assistant,
            Text = source, InteractionLanguage = "en"
        };
        using var answer = new AssistantItemViewModel(item);
        Assert.Equal(source, item.Text);
        Assert.Equal(source, answer.SourceText);
        Assert.Equal(source, answer.Text);
        Assert.Equal("en", answer.InteractionLanguage);
        Assert.Contains(ChatSessionStrings.Get("MobileTruncationNotice"), answer.SelectionText);
    }

    [Fact]
    public void JobWakeFallbacksAreLocalizedButActualJobNamesAreNot()
    {
        using var cultures = new CultureScope();
        ChatSessionStrings.Load("he");
        using var fallback = new JobWakeItemViewModel(new RemoteTranscriptItem
        {
            Id = "wake-fallback", Kind = RemoteProtocol.ItemKinds.User, Text = ""
        });
        Assert.Equal("משימה ברקע", fallback.Details.JobName);
        Assert.Equal("התקבל אות הפעלה.", fallback.Details.WakeSignal);

        using var named = new JobWakeItemViewModel(new RemoteTranscriptItem
        {
            Id = "wake-named", Kind = RemoteProtocol.ItemKinds.User,
            Author = "Lumi Job - Keep my English job name", Text = ""
        });
        Assert.Equal("Keep my English job name", named.Details.JobName);
    }

    [Theory]
    [InlineData("Modified", "שונה")]
    [InlineData("Submodule", "תת־מודול")]
    [InlineData("Conflicted", "התנגשות")]
    [InlineData("future-kind", "future-kind")]
    public void GitKindDisplayDoesNotChangeTheWireValue(string kind, string expected)
    {
        using var cultures = new CultureScope();
        ChatSessionStrings.Load("he");
        var file = new RemoteGitFile { Kind = kind };
        Assert.Equal(expected, MobileChatViewModel.GitKindDisplay(file.Kind));
        Assert.Equal(kind, file.Kind);
    }

    [Theory]
    [InlineData("en", "Enter a valid computer address.")]
    [InlineData("he", "יש להזין כתובת מחשב תקינה.")]
    public async Task MalformedManualAddressUsesLocalizedErrorWithoutContactingOrPairing(
        string language, string expectedError)
    {
        using var cultures = new CultureScope();
        var store = new MemorySettingsStore();
        store.Settings.Language = language;
        var handler = new UnexpectedRequestHandler();
        var client = new LumiRemoteClient("localization-test", "Test phone", handler);
        await using var shell = new MobileShellViewModel(client: client, store: store, post: action => action());
        var originalBaseUrl = client.BaseUrl;
        var originalToken = client.Token;
        shell.Connect.ManualAddress = "not a valid address";

        await shell.Connect.ConnectManuallyCommand.ExecuteAsync(null);

        Assert.Equal(expectedError, shell.Connect.ErrorText);
        Assert.Equal(0, handler.RequestCount);
        Assert.True(shell.Connect.IsFindStep);
        Assert.Null(shell.Connect.StatusText);
        Assert.False(shell.IsPaired);
        Assert.Equal(originalBaseUrl, client.BaseUrl);
        Assert.Equal(originalToken, client.Token);
        Assert.Empty(store.Settings.BaseUrl);
        Assert.Empty(store.Settings.Token);
    }

    private static Dictionary<string, string> ReadCatalog(ResourceManager manager, CultureInfo culture)
    {
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set!.Cast<DictionaryEntry>().ToDictionary(
            entry => (string)entry.Key, entry => Assert.IsType<string>(entry.Value), StringComparer.Ordinal);
    }

    private static string[] FormatArguments(string value) =>
        Regex.Matches(value, @"(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})")
            .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();

    private sealed class MemorySettingsStore : IMobileSettingsStore
    {
        public MobileConnectionSettings Settings { get; private set; } = new()
        {
            DeviceId = "localization-test", DeviceName = "Test phone", Language = "en"
        };
        public MobileConnectionSettings Load() => Settings;
        public void Save(MobileConnectionSettings settings) => Settings = settings;
    }

    private sealed class UnexpectedRequestHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("Malformed addresses must not issue requests.");
        }
    }

    private sealed class UpdateableWebHost : IMobileHostEnvironment
    {
        public bool HasFixedEndpoint => true;
        public string? FixedBaseUrl => "https://lumi.test";
        public string FixedEndpointName => "Test PC";
        public Action? ApplyWebAppUpdate => () => { };
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
        private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
            CultureInfo.DefaultThreadCurrentCulture = _defaultCulture;
            CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
        }
    }
}
