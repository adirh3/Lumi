using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Lumi.Mobile.Localization;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using Xunit;

namespace Lumi.Mobile.Tests;

public sealed class MobileLocalizationTests
{
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
    [InlineData("en", "Enter a valid PC address.")]
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
