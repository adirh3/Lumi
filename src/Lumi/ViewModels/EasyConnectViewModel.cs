using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Byok;
using Microsoft.Extensions.AI;

namespace Lumi.ViewModels;

/// <summary>Where the add/manage AI account dialog currently is.</summary>
public enum ConnectStage { Gallery, Account, Models, Connecting, Done }

/// <summary>A provider's identity tile: a monogram on its own colour, or a neutral glyph.</summary>
public sealed class ProviderBadge
{
    private static readonly Dictionary<string, ProviderBadge> Cache = new(StringComparer.Ordinal);
    private static readonly Color GitHubTop = Color.Parse("#3D444D");
    private static readonly Color GitHubBottom = Color.Parse("#0D1117");
    private static readonly Color NeutralTop = Color.Parse("#52525B");
    private static readonly Color NeutralBottom = Color.Parse("#232326");

    public static ProviderBadge GitHub { get; } = new("", Gradient(GitHubTop, GitHubBottom), isGitHub: true, isCustom: false);
    public static ProviderBadge Custom { get; } = new("", null, isGitHub: false, isCustom: true);
    public static ProviderBadge ChatGpt { get; } = new("", Gradient(Color.Parse("#2F2F2F"), Color.Parse("#0D0D0D")),
        isGitHub: false, isCustom: false, isChatGpt: true);

    private ProviderBadge(string monogram, IBrush? fill, bool isGitHub, bool isCustom, bool isChatGpt = false)
    {
        Monogram = monogram;
        Fill = fill;
        IsGitHub = isGitHub;
        IsCustom = isCustom;
        IsChatGpt = isChatGpt;
    }

    public string Monogram { get; }
    /// <summary>The tile's fill; null for the outlined custom-provider tile.</summary>
    public IBrush? Fill { get; }
    public bool IsFilled => Fill is not null;
    public bool IsGitHub { get; }
    public bool IsCustom { get; }
    public bool IsChatGpt { get; }
    public bool ShowMonogram => !IsGitHub && !IsCustom && !IsChatGpt;

    public static ProviderBadge For(ProviderProfile? profile)
    {
        if (profile is null || profile.IsCustom)
            return Custom;
        if (profile.IsCopilot)
            return GitHub;
        if (profile.IsChatGpt)
            return ChatGpt;
        lock (Cache)
        {
            if (Cache.TryGetValue(profile.Id, out var cached))
                return cached;
            var fill = profile.Hue is { } hue && Color.TryParse(hue, out var color)
                ? Gradient(Mix(color, Colors.White, 0.14), Mix(color, Colors.Black, 0.22))
                : Gradient(NeutralTop, NeutralBottom);
            var badge = new ProviderBadge(profile.Monogram, fill, isGitHub: false, isCustom: false);
            Cache[profile.Id] = badge;
            return badge;
        }
    }

    private static Color Mix(Color color, Color target, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R + (target.R - color.R) * amount),
        (byte)Math.Round(color.G + (target.G - color.G) * amount),
        (byte)Math.Round(color.B + (target.B - color.B) * amount));

    private static IBrush Gradient(Color top, Color bottom) => new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, top), new ImmutableGradientStop(1, bottom)],
        startPoint: RelativePoint.TopLeft, endPoint: RelativePoint.BottomRight);
}

public sealed class ProviderCard
{
    public ProviderCard(ProviderProfile profile, bool isConnected, Action<ProviderProfile> select)
    {
        Profile = profile;
        IsConnected = isConnected;
        Badge = ProviderBadge.For(profile);
        OpenCommand = new RelayCommand(() => select(profile));
    }

    public ProviderProfile Profile { get; }
    public ProviderBadge Badge { get; }
    public bool IsConnected { get; }
    public string Name => Profile.Name;
    public string Description => Profile.Description;
    public string Billing => Profile.Billing;
    public string MethodLabel => Profile switch
    {
        { IsCopilot: true } => Loc.Get("EasyConnect_MethodGitHub"),
        { HasBrowserSignIn: true } => Loc.Get("EasyConnect_MethodSignIn"),
        { IsLocal: true } => Loc.Get("EasyConnect_MethodLocal"),
        { IsCustom: true } => Loc.Get("EasyConnect_MethodCustom"),
        { Id: "minimax-plan" } => Loc.Get("EasyConnect_MethodSubscriptionKey"),
        _ => Loc.Get("EasyConnect_MethodApiKey")
    };
    public IRelayCommand OpenCommand { get; }
}

public sealed class ProviderGroup(string title, string caption, IReadOnlyList<ProviderCard> providers)
{
    public string Title { get; } = title;
    public string Caption { get; } = caption;
    public IReadOnlyList<ProviderCard> Providers { get; } = providers;
}

public partial class ConnectedProviderCard : ObservableObject
{
    public ConnectedProviderCard(
        ByokEndpoint endpoint, ProviderProfile? profile, int modelCount, bool isDefault,
        Action<ByokEndpoint> manage, Func<ConnectedProviderCard, Task> makeDefault,
        Func<ConnectedProviderCard, Task> disconnect, Action<Uri> open)
    {
        Endpoint = endpoint;
        Profile = profile;
        ModelCount = modelCount;
        IsDefault = isDefault;
        Badge = ProviderBadge.For(profile);
        ManageCommand = new RelayCommand(() => manage(endpoint));
        MakeDefaultCommand = new AsyncRelayCommand(() => makeDefault(this));
        UsageCommand = new RelayCommand(() =>
        {
            if (profile?.UsageUrl is { } url)
                open(new Uri(url));
        });
        RequestDisconnectCommand = new RelayCommand(() => IsConfirmingDisconnect = true);
        CancelDisconnectCommand = new RelayCommand(() => IsConfirmingDisconnect = false);
        ConfirmDisconnectCommand = new AsyncRelayCommand(() => disconnect(this));
    }

    public ByokEndpoint Endpoint { get; }
    public ProviderProfile? Profile { get; }
    public ProviderBadge Badge { get; }
    public int ModelCount { get; }
    public bool IsDefault { get; }
    public string Name => Endpoint.Name;
    public bool IsExpired => Endpoint.CredentialExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow;
    public bool CanMakeDefault => !IsDefault && Endpoint.IsEnabled && !IsExpired && ModelCount > 0;
    public bool HasUsageLink => Profile?.UsageUrl is not null;
    public string Billing => Profile?.Billing ?? Loc.Get("EasyConnect_custom_Billing");
    public string Detail => IsExpired
        ? Loc.Get(Profile?.IsChatGpt == true ? "ChatGpt_ConnectionExpired" : "EasyConnect_ConnectionExpired")
        : (Endpoint.Account?.Email is { Length: > 0 } email ? email + " · " : "")
          + (ModelCount == 1 ? Loc.Get("EasyConnect_ConnectedModel")
              : string.Format(Loc.Get("EasyConnect_ConnectedModels"), ModelCount)) + " · " + Billing;
    public string DisconnectPrompt => Profile?.IsChatGpt == true ? Loc.Get("ChatGpt_DisconnectPrompt")
        : string.Format(Loc.Get("EasyConnect_DisconnectPrompt"), Name);
    [ObservableProperty] private bool _isConfirmingDisconnect;
    public IRelayCommand ManageCommand { get; }
    public IAsyncRelayCommand MakeDefaultCommand { get; }
    public IRelayCommand UsageCommand { get; }
    public IRelayCommand RequestDisconnectCommand { get; }
    public IRelayCommand CancelDisconnectCommand { get; }
    public IAsyncRelayCommand ConfirmDisconnectCommand { get; }
}

public partial class ConnectModelChoice(ProviderModel model) : ObservableObject
{
    internal ProviderModel SourceModel { get; } = model;
    public ProviderModel Model { get; private set; } = model;
    public string Name => Model.Name;
    public string Id => Model.Id;
    public bool ShowId => !string.Equals(Model.Name, Model.Id, StringComparison.Ordinal);
    public bool IsFree => Model.IsFree;
    public bool HasContext => Model.ContextTokens is > 0;
    public string ContextLabel => Model.ContextTokens is { } tokens and > 0
        ? string.Format(Loc.Get("EasyConnect_ContextChip"), FormatTokens(tokens)) : "";
    [ObservableProperty] private bool _isSelected;

    internal void UpdateFoundryReasoning(string wireApi)
        => Model = ProviderConnectionService.WithFoundryReasoning(SourceModel, wireApi);

    internal static string FormatTokens(int tokens) => tokens >= 1_000_000
        ? Loc.Get(nameof(Loc.ModelPicker_CompactMillions), (tokens / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture))
        : tokens >= 1_000
            ? Loc.Get(nameof(Loc.ModelPicker_CompactThousands), (tokens / 1_000d).ToString("0", CultureInfo.CurrentCulture))
            : tokens.ToString(CultureInfo.CurrentCulture);
}

/// <summary>
/// Owns Lumi's AI accounts: the GitHub Copilot account (the default), the external accounts
/// connected through Easy Connect, and the add/manage dialog that connects them.
/// </summary>
public partial class EasyConnectViewModel : ObservableObject, IDisposable
{
    private static readonly ProviderCategory[] GalleryOrder =
    [
        ProviderCategory.Recommended, ProviderCategory.SignIn, ProviderCategory.Plans,
        ProviderCategory.Apis, ProviderCategory.Local, ProviderCategory.Custom
    ];

    private readonly DataStore _store;
    private readonly CopilotService _copilot;
    private readonly ISecureKeyStore _keys;
    private readonly ProviderConnectionService _connections;
    private readonly ProviderOAuthService _oauth;
    private readonly ChatGptAuthService _chatGpt;
    private readonly bool _ownsChatGpt;
    private readonly ChatGptPlanBridge _bridge;
    private readonly Action<Uri> _openUrl;
    private readonly Action _saved;
    private CancellationTokenSource? _operation;
    private ByokEndpoint? _reconnecting;
    private string? _pendingKey;
    private DateTimeOffset? _expiresAt;
    private ChatGptSignIn? _chatGptSignIn;
    private bool _disposed;
    private bool _isCommitting;
    private bool _isCurrentDefaultEndpoint;
    private bool _isAuthorizing;
    private string _overviewSignature = "";
    private string _draftEndpointId = Guid.NewGuid().ToString("N");
    private GitHubLoginViewModel? _loginVM;

    /// <summary>Supplies the model ids Lumi currently offers, so GitHub Copilot can become the default again.</summary>
    public Func<IReadOnlyList<string>>? CopilotModels { get; set; }

    public GitHubLoginViewModel? LoginVM
    {
        get => _loginVM;
        set
        {
            if (ReferenceEquals(_loginVM, value))
                return;
            if (_loginVM is not null)
                _loginVM.PropertyChanged -= OnLoginPropertyChanged;
            _loginVM = value;
            if (_loginVM is not null)
                _loginVM.PropertyChanged += OnLoginPropertyChanged;
            OnPropertyChanged();
            RefreshGitHub();
            RefreshProviders();
        }
    }

    // ── Overview ──
    public ObservableCollection<ConnectedProviderCard> Connections { get; } = [];
    public bool HasUsableConnections => GetUsableModels().Any();
    public ProviderBadge GitHubBadge => ProviderBadge.GitHub;
    public bool IsGitHubSignedIn => LoginVM?.IsAuthenticated == true;
    public bool IsGitHubSigningIn => LoginVM?.IsSigningIn == true;
    public bool ShowGitHubSignIn => !IsGitHubSignedIn;
    public bool ShowGitHubRecommended => !IsGitHubSignedIn;
    public bool HasGitHubDeviceCode => LoginVM?.HasDeviceCode == true;
    public string GitHubDeviceCode => LoginVM?.DeviceCode ?? "";
    public bool IsGitHubCodeCopied => LoginVM?.DeviceCodeCopied == true;
    public string GitHubError => LoginVM?.ErrorText ?? "";
    public bool HasGitHubError => !string.IsNullOrWhiteSpace(GitHubError);
    public bool IsGitHubDefault => IsGitHubSignedIn && !_store.Data.Settings.UseBYOKOnly
        && !ByokConfigHelper.IsByokModel(_store.Data.Settings.PreferredModel);
    public bool CanMakeGitHubDefault => IsGitHubSignedIn && !_store.Data.Settings.UseBYOKOnly && !IsGitHubDefault
        && PickCopilotDefault() is not null;
    public bool HasGitHubUsage => IsGitHubSignedIn && !string.IsNullOrWhiteSpace(GitHubUsage);
    public string GitHubUsageText => string.Format(Loc.Get("EasyConnect_GitHubUsage"), GitHubUsage);
    public string GitHubSignInLabel => IsGitHubSigningIn ? Loc.Get("EasyConnect_GitHubOpening") : Loc.Get("EasyConnect_GitHubSignIn");
    public string GitHubDescription => IsGitHubSignedIn
        ? string.IsNullOrWhiteSpace(LoginVM?.GitHubLogin)
            ? Loc.Get("EasyConnect_GitHubConnected")
            : string.Format(Loc.Get("EasyConnect_GitHubSignedIn"), LoginVM!.GitHubLogin)
        : IsGitHubSigningIn ? Loc.Get("EasyConnect_GitHubSigningIn") : Loc.Get("EasyConnect_GitHubSignedOut");

    [ObservableProperty] private string? _gitHubUsage;

    // ── ChatGPT ──
    public ProviderBadge ChatGptBadge => ProviderBadge.ChatGpt;
    /// <summary>Offers ChatGPT right under GitHub Copilot until an account is connected.</summary>
    public bool ShowChatGptOffer => !_store.Data.Settings.ByokEndpoints.Any(ByokConfigHelper.IsChatGptEndpoint);

    // ── Dialog and gallery ──
    public ObservableCollection<ProviderCard> Providers { get; } = [];
    public ObservableCollection<ProviderGroup> ProviderGroups { get; } = [];
    public bool HasProviderResults => Providers.Count > 0;
    public string NoProviderResultsText => string.Format(Loc.Get("EasyConnect_NoProviders"), Search.Trim());

    [ObservableProperty] private bool _isDialogOpen;
    [ObservableProperty] private ConnectStage _stage;
    [ObservableProperty] private bool _isManaging;
    [ObservableProperty] private bool _isConfirmingRemove;
    [ObservableProperty] private string _search = "";

    public bool IsGallery => Stage == ConnectStage.Gallery;
    public bool IsAccountStage => Stage == ConnectStage.Account;
    public bool IsModelStage => Stage == ConnectStage.Models;
    public bool IsConnectingStage => Stage == ConnectStage.Connecting;
    public bool IsComplete => Stage == ConnectStage.Done;
    public bool ShowProviderHeader => Stage != ConnectStage.Gallery;
    public bool ShowStepper => !IsManaging && Stage is not ConnectStage.Gallery;
    public bool IsAccountStepActive => Stage == ConnectStage.Account;
    public bool IsAccountStepDone => Stage > ConnectStage.Account;
    public bool IsModelStepActive => Stage == ConnectStage.Models;
    public bool IsModelStepDone => Stage > ConnectStage.Models;
    public bool IsReadyStepActive => Stage == ConnectStage.Connecting;
    public bool IsReadyStepDone => Stage == ConnectStage.Done;
    public string DialogTitle => Stage == ConnectStage.Gallery
        ? Loc.Get("EasyConnect_DialogTitle")
        : string.Format(Loc.Get(IsManaging ? "EasyConnect_ManageTitle" : "EasyConnect_ConnectTitle"), SelectedName);

    // ── Selected provider ──
    [ObservableProperty] private ProviderProfile? _selectedProvider;
    public ProviderBadge SelectedBadge => ProviderBadge.For(SelectedProvider);
    public bool IsCopilot => SelectedProvider?.IsCopilot == true;
    public bool IsChatGpt => SelectedProvider?.IsChatGpt == true;
    /// <summary>ChatGPT is sign-in only: its plan has no API key to enter instead.</summary>
    public bool ShowKeyAlternative => !IsChatGpt;
    public bool ShowGenericSignIn => ShowSignInChoice && !IsChatGpt;
    public bool ShowChatGptSignIn => ShowSignInChoice && IsChatGpt;
    public bool ShowReplaceKey => HasKey && !IsChatGpt;
    /// <summary>The "You're using your ChatGPT plan" confirmation OpenAI asks for after a first sign-in.</summary>
    public bool ShowPlanConfirmation => IsComplete && IsChatGpt && !IsManaging;
    public bool IsCustom => SelectedProvider?.IsCustom == true;
    public bool IsLocal => SelectedProvider?.IsLocal == true;
    public bool IsFoundry => SelectedProvider?.IsFoundry == true;
    public bool CanEditEndpoint => IsCustom || IsLocal || IsFoundry;
    public bool HasKey => SelectedProvider is not null && RequiresApiKey && !IsCopilot;
    public string SelectedName => SelectedProvider?.Name ?? "";
    public string SelectedDescription => SelectedProvider?.Description ?? "";
    public string SelectedBilling => SelectedProvider?.Billing ?? "";
    public string SelectedNotice => SelectedProvider?.Notice ?? "";
    public bool HasUsageLink => IsManaging && SelectedProvider?.UsageUrl is not null;
    public string UsageLinkText => Loc.Get(IsChatGpt ? "ChatGpt_ManageUsage" : "EasyConnect_Usage");

    // ── Account step ──
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _baseUrl = "";
    [ObservableProperty] private string _providerType = "openai";
    [ObservableProperty] private string _wireApi = "completions";
    [ObservableProperty] private string _azureApiVersion = "2024-10-21";
    [ObservableProperty] private bool _requiresApiKey = true;
    [ObservableProperty] private bool _useKeyInstead;
    [ObservableProperty] private bool _allowPlaintextStorage;
    [ObservableProperty] private string _authorizationUrl = "";

    public bool CanSignIn => SelectedProvider?.HasBrowserSignIn == true;
    public bool ShowSignInChoice => CanSignIn && HasKey && !UseKeyInstead && !IsWaitingForBrowser;
    public bool ShowKeyEntry => HasKey && (!CanSignIn || UseKeyInstead) && !IsWaitingForBrowser;
    public bool ShowKeyGuide => ShowKeyEntry && HasKeyLink && !IsLocal && !IsFoundry;
    public bool ShowSignInInstead => CanSignIn && UseKeyInstead && !IsWaitingForBrowser;
    public bool IsWaitingForBrowser => IsWorking && (_isAuthorizing || HasAuthorizationUrl);
    public bool HasAuthorizationUrl => !string.IsNullOrWhiteSpace(AuthorizationUrl);
    public bool IsFindingModels => IsWorking && Stage == ConnectStage.Account && !IsWaitingForBrowser;
    public bool ShowContinue => Stage == ConnectStage.Account && (!CanSignIn || UseKeyInstead) && !IsWaitingForBrowser;
    public bool HasKeyLink => SelectedProvider?.KeyUrl is not null;
    public bool HasExistingKey => _reconnecting is { ApiKeyMode: not ByokApiKeyMode.None };
    public string KeyLabel => IsFoundry ? Loc.Get("EasyConnect_FoundryKey")
        : SelectedProvider?.Id == "minimax-plan" ? Loc.Get("EasyConnect_SubscriptionKey")
        : HasKeyLink && !IsLocal ? Loc.Get("EasyConnect_KeyStep2") : Loc.Get("EasyConnect_ApiKey");
    public string KeyPlaceholder => HasExistingKey
        ? Loc.Get("EasyConnect_KeepKeyPlaceholder") : Loc.Get("EasyConnect_KeyPlaceholder");
    public string ContinueWithText => string.Format(Loc.Get("EasyConnect_ContinueWith"), SelectedName);
    public string SignInHint => IsChatGpt ? Loc.Get("ChatGpt_SignInHint")
        : string.Format(Loc.Get("EasyConnect_SignInHint"), SelectedName);
    public string WaitingHint => string.Format(Loc.Get("EasyConnect_WaitingHint"), SelectedName);
    public string GetKeyText => string.Format(Loc.Get("EasyConnect_GetKeyAt"), SelectedName);
    public string LocalInstallText => string.Format(Loc.Get("EasyConnect_LocalStep1"), SelectedName);
    public string LocalDownloadText => string.Format(Loc.Get("EasyConnect_DownloadLocal"), SelectedName);
    public string ServerAddressLabel => Loc.Get(IsFoundry ? "EasyConnect_FoundryEndpoint" : "EasyConnect_ServerAddress");
    public string ServerAddressPlaceholder => IsFoundry ? "https://your-resource.services.ai.azure.com" : "https://…";
    public string ManualModelLabel => Loc.Get(IsFoundry ? "EasyConnect_FoundryAddDeployment" : "EasyConnect_ManualModel");
    public string ManualModelPlaceholder => Loc.Get(IsFoundry ? "EasyConnect_FoundryDeploymentName" : "EasyConnect_ModelId");
    public string EmptyModelsHint => Loc.Get(IsFoundry ? "EasyConnect_FoundryEmptyModels" : "EasyConnect_EmptyModels");
    public string StorageHint => _keys.IsSupported ? Loc.Get("EasyConnect_SecureStorage") : Loc.Get("EasyConnect_StorageUnavailable");
    public bool HasSecureStorage => _keys.IsSupported;
    public bool CanSaveCredential => _keys.IsSupported || (AllowPlaintextStorage && !IsChatGpt);
    public bool ShowPlaintextConsent => HasKey && !HasSecureStorage && !KeepsSavedKey && !IsChatGpt;
    private bool KeepsSavedKey => HasKey && _reconnecting is { ApiKeyMode: not ByokApiKeyMode.None }
        && string.IsNullOrWhiteSpace(ApiKey) && _pendingKey is null;
    public bool IsOpenAiType => ProviderType == "openai";
    public bool IsAzureType => ProviderType == "azure";
    public bool IsAnthropicType => ProviderType == "anthropic";
    public bool ShowWireChoice => (IsCustom || IsFoundry) && !IsAnthropicType;
    public bool IsCompletionsWire => WireApi != "responses";
    public bool IsResponsesWire => WireApi == "responses";
    public bool CanDiscover => SelectedProvider is not null && !IsCopilot && !IsWorking
        && (!IsFoundry || !string.IsNullOrWhiteSpace(BaseUrl))
        && (!HasKey || !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrEmpty(_pendingKey)
            || _chatGptSignIn is not null || _reconnecting is not null);

    // ── Model step ──
    [ObservableProperty] private string _modelSearch = "";
    [ObservableProperty] private string _manualModel = "";
    [ObservableProperty] private bool _isAddingManualModel;
    [ObservableProperty] private bool _makeDefault;
    [ObservableProperty] private bool _skipModelCheck;
    public string VerifyCostText => Loc.Get(SkipModelCheck ? "EasyConnect_SkipModelCheckHint"
        : IsManaging ? "EasyConnect_VerifyCostSave" : "EasyConnect_VerifyCost");
    public ObservableCollection<ConnectModelChoice> Models { get; } = [];
    public IEnumerable<ConnectModelChoice> VisibleModels => Models.Where(model =>
        string.IsNullOrWhiteSpace(ModelSearch) || model.Name.Contains(ModelSearch, StringComparison.OrdinalIgnoreCase)
        || model.Id.Contains(ModelSearch, StringComparison.OrdinalIgnoreCase));
    public bool HasModels => Models.Count > 0;
    public bool ShowModelSearch => Models.Count > 6;
    public bool ShowNoModels => Stage == ConnectStage.Models && Models.Count == 0;
    public string SelectedCount
    {
        get
        {
            var count = Models.Count(model => model.IsSelected);
            return count == 1 ? Loc.Get("EasyConnect_OneSelected") : string.Format(Loc.Get("EasyConnect_SelectedCount"), count);
        }
    }
    public string ModelCaption => IsFoundry ? Loc.Get("EasyConnect_FoundryModelsCaption") : IsManaging
        ? string.Format(Loc.Get("EasyConnect_ManageModelsCaption"), SelectedName)
        : Loc.Get(IsChatGpt ? "ChatGpt_ModelsCaption" : "EasyConnect_ModelSelectionHint");
    public bool ShowDefaultChoice => !_isCurrentDefaultEndpoint;
    public bool IsCurrentDefault => _isCurrentDefaultEndpoint;
    public bool CanToggleDefault => HasUsableDefault(_reconnecting?.Id);
    public string DefaultHint => !CanToggleDefault ? Loc.Get("EasyConnect_DefaultRequired")
        : IsGitHubDefault ? Loc.Get("EasyConnect_DefaultKeepsGitHub")
        : Loc.Get("EasyConnect_DefaultKeepsCurrent");
    public bool CanSave => !IsWorking && Models.Any(model => model.IsSelected);
    public string PrimaryActionText => Loc.Get(SkipModelCheck ? "EasyConnect_SaveWithoutTesting"
        : IsManaging ? "EasyConnect_Save" : "EasyConnect_Connect");
    public string BackText => Loc.Get(IsManaging && Stage == ConnectStage.Models ? "EasyConnect_Cancel" : "EasyConnect_Back");
    public bool ShowModelFooter => Stage == ConnectStage.Models && !IsConfirmingRemove;
    public bool ShowRemoveConfirm => Stage == ConnectStage.Models && IsConfirmingRemove;
    public string RemovePrompt => IsChatGpt ? Loc.Get("ChatGpt_DisconnectPrompt")
        : string.Format(Loc.Get("EasyConnect_DisconnectPrompt"), SelectedName);

    // ── Connecting and done ──
    [ObservableProperty] private bool _isSavingStage;
    [ObservableProperty] private string _doneText = "";
    public string ConnectingTitle => string.Format(Loc.Get("EasyConnect_ConnectingTitle"), SelectedName);
    public string TestingText => string.Format(Loc.Get("EasyConnect_TestingModel"), StarterName);
    public string ConnectingNote => Loc.Get(SkipModelCheck ? "EasyConnect_SkipModelCheckHint" : "EasyConnect_ConnectingNote");
    public string StarterName => Models.FirstOrDefault(model => model.IsSelected)?.Name ?? "";
    public bool IsTestingStage => !IsSavingStage;
    public string DoneTitle => ShowPlanConfirmation ? Loc.Get("ChatGpt_DoneTitle")
        : string.Format(Loc.Get(IsManaging ? "EasyConnect_SavedTitle" : "EasyConnect_DoneTitle"), SelectedName);
    public string DoneActionText => Loc.Get(ShowPlanConfirmation ? "ChatGpt_GotIt" : "EasyConnect_Done");

    // ── Shared status ──
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool ShowOverviewError => HasError && !IsDialogOpen;
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status) && !IsWorking;
    public bool CanCancel => IsWorking && !_isCommitting;
    public bool CanGoBack => !IsWorking && Stage is ConnectStage.Account or ConnectStage.Models;
    public EasyConnectViewModel(
        DataStore store, CopilotService copilot, ISecureKeyStore keys, Action saved,
        ProviderConnectionService? connections = null, ProviderOAuthService? oauth = null,
        Action<Uri>? openUrl = null, ChatGptAuthService? chatGpt = null, ChatGptPlanBridge? bridge = null)
    {
        _store = store;
        _copilot = copilot;
        _keys = keys;
        _saved = saved;
        _connections = connections ?? new ProviderConnectionService();
        _oauth = oauth ?? new ProviderOAuthService();
        _ownsChatGpt = chatGpt is null;
        _chatGpt = chatGpt ?? new ChatGptAuthService();
        _bridge = bridge ?? ChatGptPlanBridge.Shared;
        _openUrl = openUrl ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        _store.IndexSaved += OnIndexSaved;
        _bridge.SignInRequired += OnChatGptSignInRequired;
        Refresh();
    }

    /// <summary>Marks a ChatGPT connection whose sign-in can no longer be renewed, so it asks to sign in again.</summary>
    private void OnChatGptSignInRequired(string endpointId) => Dispatcher.UIThread.Post(async () =>
    {
        if (_disposed || _store.Data.Settings.ByokEndpoints.FirstOrDefault(endpoint => endpoint.Id == endpointId)
                is not { } endpoint || IsExpired(endpoint))
            return;
        endpoint.CredentialExpiresAt = DateTimeOffset.UtcNow;
        await _store.SaveAsync();
        _saved();
        RefreshOverview(force: true);
    });

    private void OnIndexSaved()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
                RefreshOverview();
        });
    }

    private void OnLoginPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess())
            OnLoginChanged(args.PropertyName);
        else
            Dispatcher.UIThread.Post(() => OnLoginChanged(args.PropertyName));
    }

    private void OnLoginChanged(string? propertyName)
    {
        if (_disposed)
            return;
        RefreshGitHub();
        if (propertyName == nameof(GitHubLoginViewModel.IsAuthenticated))
        {
            RefreshProviders();
            OnPropertyChanged(nameof(CanToggleDefault));
            OnPropertyChanged(nameof(DefaultHint));
        }
    }

    private void RefreshGitHub()
    {
        foreach (var name in new[] { nameof(IsGitHubSignedIn), nameof(IsGitHubSigningIn), nameof(ShowGitHubSignIn),
                     nameof(ShowGitHubRecommended), nameof(HasGitHubDeviceCode), nameof(GitHubDeviceCode),
                     nameof(IsGitHubCodeCopied), nameof(GitHubError), nameof(HasGitHubError), nameof(IsGitHubDefault),
                     nameof(CanMakeGitHubDefault), nameof(HasGitHubUsage), nameof(GitHubSignInLabel), nameof(GitHubDescription) })
            OnPropertyChanged(name);
    }

    partial void OnGitHubUsageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasGitHubUsage));
        OnPropertyChanged(nameof(GitHubUsageText));
    }
    partial void OnSearchChanged(string value) => RefreshProviders();
    partial void OnModelSearchChanged(string value) => OnPropertyChanged(nameof(VisibleModels));
    partial void OnErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ShowOverviewError));
    }
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnAuthorizationUrlChanged(string value) => UpdateAccountState();
    partial void OnUseKeyInsteadChanged(bool value) => UpdateAccountState();
    partial void OnApiKeyChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _pendingKey = null;
            _expiresAt = null;
        }
        UpdateActions();
    }
    partial void OnBaseUrlChanged(string value) => UpdateActions();
    partial void OnIsWorkingChanged(bool value)
    {
        UpdateAccountState();
        OnPropertyChanged(nameof(HasStatus));
    }
    partial void OnAllowPlaintextStorageChanged(bool value) => OnPropertyChanged(nameof(CanSaveCredential));
    partial void OnProviderTypeChanged(string value)
    {
        foreach (var name in new[] { nameof(IsOpenAiType), nameof(IsAzureType), nameof(IsAnthropicType), nameof(ShowWireChoice) })
            OnPropertyChanged(name);
    }
    partial void OnWireApiChanged(string value)
    {
        OnPropertyChanged(nameof(IsCompletionsWire));
        OnPropertyChanged(nameof(IsResponsesWire));
        if (IsFoundry)
            foreach (var choice in Models)
                choice.UpdateFoundryReasoning(value);
    }
    partial void OnSkipModelCheckChanged(bool value)
    {
        OnPropertyChanged(nameof(VerifyCostText));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(ConnectingNote));
    }
    partial void OnRequiresApiKeyChanged(bool value)
    {
        if (!value)
        {
            ApiKey = "";
            _pendingKey = null;
            _expiresAt = null;
        }
        UpdateAccountState();
    }
    partial void OnIsSavingStageChanged(bool value) => OnPropertyChanged(nameof(IsTestingStage));
    partial void OnIsConfirmingRemoveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowModelFooter));
        OnPropertyChanged(nameof(ShowRemoveConfirm));
    }
    partial void OnIsManagingChanged(bool value)
    {
        foreach (var name in new[] { nameof(ShowStepper), nameof(DialogTitle), nameof(ModelCaption),
                     nameof(PrimaryActionText), nameof(HasUsageLink), nameof(DoneTitle), nameof(BackText),
                     nameof(VerifyCostText), nameof(ShowPlanConfirmation), nameof(DoneActionText) })
            OnPropertyChanged(name);
    }
    partial void OnStageChanged(ConnectStage value)
    {
        foreach (var name in new[] { nameof(IsGallery), nameof(IsAccountStage), nameof(IsModelStage),
                     nameof(IsConnectingStage), nameof(IsComplete), nameof(ShowProviderHeader), nameof(ShowStepper),
                     nameof(IsAccountStepActive), nameof(IsAccountStepDone), nameof(IsModelStepActive),
                     nameof(IsModelStepDone), nameof(IsReadyStepActive), nameof(IsReadyStepDone), nameof(DialogTitle),
                     nameof(ShowNoModels), nameof(ShowContinue), nameof(IsFindingModels), nameof(CanGoBack),
                     nameof(BackText), nameof(ShowModelFooter), nameof(ShowRemoveConfirm),
                     nameof(ShowPlanConfirmation), nameof(DoneTitle), nameof(DoneActionText) })
            OnPropertyChanged(name);
    }
    partial void OnIsDialogOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOverviewError));
        if (value)
        {
            Error = "";
            return;
        }
        if (_isCommitting)
            return;
        _operation?.Cancel();
        if (!IsWorking)
            ResetWizard();
    }
    partial void OnSelectedProviderChanged(ProviderProfile? value)
    {
        foreach (var name in new[] { nameof(SelectedBadge), nameof(IsCopilot), nameof(IsCustom), nameof(IsLocal), nameof(IsFoundry),
                     nameof(CanEditEndpoint), nameof(SelectedName), nameof(SelectedDescription), nameof(SelectedBilling),
                     nameof(SelectedNotice), nameof(HasUsageLink), nameof(UsageLinkText), nameof(KeyLabel), nameof(ContinueWithText),
                     nameof(SignInHint), nameof(WaitingHint), nameof(GetKeyText), nameof(LocalInstallText),
                     nameof(LocalDownloadText), nameof(HasKeyLink), nameof(CanSignIn), nameof(DialogTitle),
                     nameof(ModelCaption), nameof(ConnectingTitle), nameof(DoneTitle), nameof(ShowWireChoice),
                     nameof(RemovePrompt), nameof(ServerAddressLabel), nameof(ServerAddressPlaceholder),
                     nameof(ManualModelLabel), nameof(ManualModelPlaceholder), nameof(EmptyModelsHint),
                     nameof(IsChatGpt), nameof(ShowKeyAlternative), nameof(ShowPlanConfirmation), nameof(DoneActionText),
                     nameof(CanSaveCredential) })
            OnPropertyChanged(name);
        UpdateAccountState();
    }

    private void UpdateAccountState()
    {
        foreach (var name in new[] { nameof(HasKey), nameof(ShowSignInChoice), nameof(ShowKeyEntry), nameof(ShowKeyGuide),
                     nameof(ShowSignInInstead), nameof(IsWaitingForBrowser), nameof(IsFindingModels), nameof(ShowContinue),
                     nameof(HasExistingKey), nameof(KeyPlaceholder), nameof(CanGoBack), nameof(HasAuthorizationUrl),
                     nameof(ShowGenericSignIn), nameof(ShowChatGptSignIn), nameof(ShowReplaceKey) })
            OnPropertyChanged(name);
        UpdateActions();
    }

    private void UpdateActions()
    {
        OnPropertyChanged(nameof(CanDiscover));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(StarterName));
        OnPropertyChanged(nameof(TestingText));
        OnPropertyChanged(nameof(ShowPlaintextConsent));
    }

    private void NotifyModels()
    {
        OnPropertyChanged(nameof(VisibleModels));
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(ShowModelSearch));
        OnPropertyChanged(nameof(ShowNoModels));
        UpdateActions();
    }

    public void Refresh()
    {
        RefreshOverview(force: true);
        RefreshProviders();
    }

    /// <summary>Re-evaluates GitHub actions after Lumi's model catalog changes.</summary>
    public void NotifyCopilotModelsChanged() => OnPropertyChanged(nameof(CanMakeGitHubDefault));

    private void RefreshOverview(bool force = false)
    {
        var settings = _store.Data.Settings;
        var signature = string.Join('|', settings.ByokEndpoints.Select(endpoint =>
            $"{endpoint.Id}:{endpoint.Name}:{endpoint.IsEnabled}:{endpoint.ConnectionProviderId}:{endpoint.CredentialExpiresAt?.Ticks}:"
            + settings.ByokModels.Count(model => model.EndpointId == endpoint.Id && model.IsEnabled)))
            + "|" + settings.PreferredModel + "|" + settings.UseBYOKOnly;
        if (!force && signature == _overviewSignature)
            return;
        _overviewSignature = signature;
        Connections.Clear();
        foreach (var endpoint in settings.ByokEndpoints)
        {
            var models = settings.ByokModels.Where(model => model.EndpointId == endpoint.Id && model.IsEnabled).ToList();
            var isDefault = models.Any(model => ByokConfigHelper.BuildModelToken(model) == settings.PreferredModel);
            Connections.Add(new(endpoint, ExistingProfile(endpoint), models.Count, isDefault,
                Manage, MakeConnectionDefaultAsync, card => DisconnectAsync(card.Endpoint), OpenUrl));
        }
        OnPropertyChanged(nameof(HasUsableConnections));
        OnPropertyChanged(nameof(ShowChatGptOffer));
        RefreshGitHub();
        RefreshProviders();
    }

    private void RefreshProviders()
    {
        var connected = _store.Data.Settings.ByokEndpoints
            .Select(endpoint => ExistingProfile(endpoint)?.Id)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);
        Providers.Clear();
        foreach (var profile in ProviderCatalog.All.Where(profile =>
                     (!profile.IsCopilot || !IsGitHubSignedIn)
                     && (string.IsNullOrWhiteSpace(Search)
                         || profile.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                         || profile.Description.Contains(Search, StringComparison.OrdinalIgnoreCase)
                         || profile.Billing.Contains(Search, StringComparison.OrdinalIgnoreCase)
                         || profile.Notice.Contains(Search, StringComparison.OrdinalIgnoreCase))))
            Providers.Add(new(profile, connected.Contains(profile.Id), Select));

        ProviderGroups.Clear();
        foreach (var category in GalleryOrder)
        {
            var cards = Providers.Where(card => card.Profile.Category == category).ToList();
            if (cards.Count == 0)
                continue;
            var key = category.ToString();
            ProviderGroups.Add(new(Loc.Get($"EasyConnect_Group{key}"), Loc.Get($"EasyConnect_Group{key}Caption"), cards));
        }
        OnPropertyChanged(nameof(HasProviderResults));
        OnPropertyChanged(nameof(NoProviderResultsText));
    }

    // ═══ Dialog navigation ═══

    [RelayCommand]
    private void OpenGallery()
    {
        if (IsWorking)
        {
            IsDialogOpen = true;
            return;
        }
        ResetWizard();
        IsDialogOpen = true;
    }

    [RelayCommand]
    private void CloseDialog() => IsDialogOpen = false;

    [RelayCommand]
    private void Done() => IsDialogOpen = false;

    [RelayCommand]
    private void UseCustomProvider() => Select(ProviderCatalog.Find("custom")!);

    /// <summary>One click from the accounts list: opens ChatGPT's sign-in and follows it in the dialog.</summary>
    [RelayCommand]
    private async Task ConnectChatGptAsync()
    {
        if (IsWorking)
        {
            IsDialogOpen = true;
            return;
        }
        Select(ProviderCatalog.Find(ProviderCatalog.ChatGptId)!);
        if (Stage == ConnectStage.Account && !IsManaging)
            await SignInAsync();
    }

    private void ResetWizard()
    {
        _operation?.Cancel();
        AbandonChatGptSignIn(removeFromBridge: true);
        _reconnecting = null;
        _chatGptSignIn = null;
        _isCurrentDefaultEndpoint = false;
        _pendingKey = null;
        _expiresAt = null;
        _draftEndpointId = Guid.NewGuid().ToString("N");
        SelectedProvider = null;
        IsManaging = false;
        IsConfirmingRemove = false;
        UseKeyInstead = false;
        IsSavingStage = false;
        SkipModelCheck = false;
        AllowPlaintextStorage = false;
        Error = "";
        Status = "";
        AuthorizationUrl = "";
        ApiKey = "";
        DoneText = "";
        Models.Clear();
        ModelSearch = "";
        ManualModel = "";
        IsAddingManualModel = false;
        Search = "";
        Stage = ConnectStage.Gallery;
        NotifyModels();
        OnPropertyChanged(nameof(ShowDefaultChoice));
        OnPropertyChanged(nameof(IsCurrentDefault));
    }

    private void Select(ProviderProfile profile)
    {
        if (profile.IsCopilot)
        {
            IsDialogOpen = false;
            SignInGitHub();
            return;
        }
        // A connected provider opens its existing connection instead of creating a duplicate.
        if (!profile.IsCustom && _store.Data.Settings.ByokEndpoints.FirstOrDefault(endpoint =>
                ExistingProfile(endpoint)?.Id == profile.Id) is { } existing)
        {
            Manage(existing);
            return;
        }
        ResetWizard();
        SelectedProvider = profile;
        BaseUrl = profile.BaseUrl;
        ProviderType = profile.ProviderType;
        WireApi = profile.WireApi;
        RequiresApiKey = !profile.IsLocal;
        MakeDefault = !HasUsableDefault();
        Stage = ConnectStage.Account;
        RefreshDefaultChoice();
        IsDialogOpen = true;
    }

    private void Manage(ByokEndpoint endpoint)
    {
        if (IsWorking)
            return;
        ResetWizard();
        SelectedProvider = ExistingProfile(endpoint) ?? ProviderCatalog.Find("custom")!;
        _reconnecting = endpoint;
        _expiresAt = endpoint.CredentialExpiresAt;
        IsManaging = true;
        RequiresApiKey = endpoint.ApiKeyMode != ByokApiKeyMode.None;
        BaseUrl = endpoint.BaseUrl;
        ProviderType = endpoint.ProviderType;
        WireApi = endpoint.WireApi;
        AzureApiVersion = endpoint.AzureApiVersion ?? "2024-10-21";
        var settings = _store.Data.Settings;
        var prefix = endpoint.Name + " · ";
        foreach (var model in settings.ByokModels.Where(model => model.EndpointId == endpoint.Id))
        {
            var name = model.DisplayName.StartsWith(prefix, StringComparison.Ordinal)
                ? model.DisplayName[prefix.Length..] : model.DisplayName;
            AddChoice(new(model.ModelId, string.IsNullOrWhiteSpace(name) ? model.ModelId : name,
                model.DefaultContextWindowTokens, SupportedReasoningEfforts: model.SupportsReasoningEffort
                    ? model.SupportedReasoningEfforts.ToArray() : null, DefaultReasoningEffort: model.DefaultReasoningEffort),
                model.IsEnabled);
        }
        _isCurrentDefaultEndpoint = settings.ByokModels.Any(model => model.EndpointId == endpoint.Id
            && ByokConfigHelper.BuildModelToken(model) == settings.PreferredModel);
        MakeDefault = _isCurrentDefaultEndpoint || !HasUsableDefault(endpoint.Id);
        var expired = IsExpired(endpoint);
        Stage = expired ? ConnectStage.Account : ConnectStage.Models;
        if (expired)
            Status = Loc.Get(IsChatGpt ? "ChatGpt_ExpiredHint" : "EasyConnect_ExpiredHint");
        NotifyModels();
        RefreshDefaultChoice();
        UpdateAccountState();
        IsDialogOpen = true;
    }

    private void RefreshDefaultChoice()
    {
        OnPropertyChanged(nameof(ShowDefaultChoice));
        OnPropertyChanged(nameof(IsCurrentDefault));
        OnPropertyChanged(nameof(CanToggleDefault));
        OnPropertyChanged(nameof(DefaultHint));
    }

    [RelayCommand]
    private void Back()
    {
        if (IsWorking)
            return;
        Error = "";
        switch (Stage)
        {
            case ConnectStage.Account when IsManaging && !IsExpired(_reconnecting):
                // Leaving "Replace key" keeps the saved key; a half-entered replacement is discarded.
                ApiKey = "";
                _pendingKey = null;
                _expiresAt = _reconnecting!.CredentialExpiresAt;
                UseKeyInstead = false;
                Status = "";
                Stage = ConnectStage.Models;
                break;
            case ConnectStage.Account when IsManaging:
            case ConnectStage.Models when IsManaging:
                IsDialogOpen = false;
                break;
            case ConnectStage.Account:
                ResetWizard();
                break;
            case ConnectStage.Models:
                Stage = ConnectStage.Account;
                break;
        }
    }

    private static bool IsExpired(ByokEndpoint? endpoint)
        => endpoint?.CredentialExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow;

    [RelayCommand]
    private void ToggleKeyEntry() => UseKeyInstead = !UseKeyInstead;

    [RelayCommand]
    private void ReplaceKey()
    {
        if (IsWorking)
            return;
        Error = "";
        Stage = ConnectStage.Account;
    }

    [RelayCommand]
    private void SetProviderType(string type)
    {
        ProviderType = type;
        if (type == "anthropic")
            WireApi = "completions";
    }

    [RelayCommand]
    private void SetWireApi(string wireApi) => WireApi = wireApi;

    [RelayCommand]
    private void Cancel()
    {
        if (!_isCommitting)
            _operation?.Cancel();
    }

    [RelayCommand]
    private void OpenKeyPage()
    {
        if (SelectedProvider?.KeyUrl is { } url)
            OpenUrl(new Uri(url));
    }

    [RelayCommand]
    private void OpenUsage()
    {
        if (SelectedProvider?.UsageUrl is { } url)
            OpenUrl(new Uri(url));
    }

    [RelayCommand]
    private void OpenAuthorization()
    {
        if (Uri.TryCreate(AuthorizationUrl, UriKind.Absolute, out var uri))
            OpenUrl(uri);
    }

    private void OpenUrl(Uri uri)
    {
        try { _openUrl(uri); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EasyConnect] Browser launch failed ({ex.GetType().Name}).");
            Error = Loc.Get("EasyConnect_BrowserFailed");
        }
    }

    // ═══ GitHub Copilot account ═══

    [RelayCommand]
    private void SignInGitHub()
    {
        if (LoginVM is { IsAuthenticated: false, IsSigningIn: false } login)
            login.SignInCommand.Execute(null);
    }

    [RelayCommand]
    private void CancelGitHubSignIn() => LoginVM?.CancelSignIn();

    [RelayCommand]
    private void CopyGitHubCode() => LoginVM?.CopyDeviceCodeCommand.Execute(null);

    [RelayCommand]
    private void OpenGitHubVerification() => LoginVM?.OpenVerificationUrlCommand.Execute(null);

    [RelayCommand]
    private async Task SignOutGitHubAsync()
    {
        if (LoginVM is not { } login)
            return;
        await login.SignOutCommand.ExecuteAsync(null);
        if (login.IsAuthenticated)
            return;
        // Keep Lumi usable on a connected account when Copilot is no longer available.
        var settings = _store.Data.Settings;
        if (!ByokConfigHelper.IsByokModel(settings.PreferredModel)
            && GetUsableModels().FirstOrDefault() is { } fallback)
        {
            settings.PreferredModel = ByokConfigHelper.BuildModelToken(fallback);
            await _store.SaveAsync();
            _saved();
        }
        RefreshOverview(force: true);
    }

    [RelayCommand]
    private async Task MakeGitHubDefaultAsync()
    {
        if (!IsGitHubSignedIn || _store.Data.Settings.UseBYOKOnly || PickCopilotDefault() is not { } model)
            return;
        _store.Data.Settings.PreferredModel = model;
        await _store.SaveAsync();
        _saved();
        RefreshOverview(force: true);
    }

    private async Task MakeConnectionDefaultAsync(ConnectedProviderCard card)
    {
        var settings = _store.Data.Settings;
        if (!card.CanMakeDefault
            || GetUsableModels().FirstOrDefault(model => model.EndpointId == card.Endpoint.Id) is not { } model)
            return;
        settings.PreferredModel = ByokConfigHelper.BuildModelToken(model);
        await _store.SaveAsync();
        _saved();
        RefreshOverview(force: true);
    }

    private string? PickCopilotDefault()
    {
        var models = CopilotModels?.Invoke()
            .Where(model => !string.IsNullOrWhiteSpace(model) && !ByokConfigHelper.IsByokModel(model))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return models is { Count: > 0 } ? ChatViewModel.PickBestModel(models) : null;
    }

    /// <summary>
    /// True when Lumi already has a working default: a signed-in GitHub Copilot account, or a
    /// valid external model (other than <paramref name="excludingEndpointId"/>'s).
    /// </summary>
    private bool HasUsableDefault(string? excludingEndpointId = null)
    {
        var settings = _store.Data.Settings;
        var preferred = settings.PreferredModel;
        if (!ByokConfigHelper.IsByokModel(preferred))
            return IsGitHubSignedIn && !settings.UseBYOKOnly;
        return GetUsableModels().Any(model =>
            model.EndpointId != excludingEndpointId
            && ByokConfigHelper.BuildModelToken(model) == preferred);
    }

    private IEnumerable<ByokModel> GetUsableModels()
    {
        var settings = _store.Data.Settings;
        return ByokConfigHelper.GetValidModels(settings).Where(model =>
            !IsExpired(settings.ByokEndpoints.FirstOrDefault(endpoint => endpoint.Id == model.EndpointId)));
    }

    private static ProviderProfile? ExistingProfile(ByokEndpoint endpoint)
    {
        if (ProviderCatalog.Find(endpoint.ConnectionProviderId) is { } saved)
            return saved;
        var inferred = ProviderCatalog.FromUrl(endpoint.BaseUrl);
        // An endpoint alone cannot distinguish a MiniMax subscription key from a PAYG key.
        return inferred?.Id == "minimax-plan" ? null : inferred;
    }

    // ═══ Connecting ═══

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsWorking || SelectedProvider?.OAuth is not { } kind)
            return;
        await RunAsync(async ct =>
        {
            Status = "";
            if (kind == ProviderOAuthKind.ChatGpt)
            {
                await SignInChatGptAsync(ct);
                return;
            }
            SetAuthorizing(true);
            var credential = await _oauth.AuthenticateAsync(
                new(kind, kind == ProviderOAuthKind.Poe ? ProviderCatalog.PoeClientId : null),
                async uri => await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AuthorizationUrl = uri.AbsoluteUri;
                    _openUrl(uri);
                }), ct);
            ct.ThrowIfCancellationRequested();
            ApiKey = "";
            _pendingKey = credential.ApiKey;
            _expiresAt = credential.ExpiresAt;
            SetAuthorizing(false);
            AuthorizationUrl = "";
            await DiscoverCoreAsync(ct);
        });
        SetAuthorizing(false);
    }

    private void SetAuthorizing(bool value)
    {
        if (_isAuthorizing == value)
            return;
        _isAuthorizing = value;
        UpdateAccountState();
    }

    private string DraftEndpointId => _reconnecting?.Id ?? _draftEndpointId;

    private async Task SignInChatGptAsync(CancellationToken ct)
    {
        // The rotating refresh token lives only in the OS credential store.
        if (!_keys.IsSupported)
            throw new ProviderConnectionException(Loc.Get("ChatGpt_StorageRequired"));
        var settings = _store.Data.Settings;
        if (string.IsNullOrWhiteSpace(settings.ChatGptHostId))
        {
            // OpenAI asks for this computer's identifier to be chosen and saved before the first sign-in.
            settings.ChatGptHostId = "urn:uuid:" + Guid.NewGuid().ToString("D");
            await _store.SaveAsync();
        }
        var existing = _reconnecting?.Account is { ClientId.Length: > 0 } saved
            ? new ChatGptAccount(saved.ClientId, saved.Subject, saved.Email) : null;
        AbandonChatGptSignIn(removeFromBridge: false);
        SetAuthorizing(true);
        var signIn = await _chatGpt.SignInAsync(settings.ChatGptHostId, existing,
            async uri => await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AuthorizationUrl = uri.AbsoluteUri;
                _openUrl(uri);
            }), ct);
        ct.ThrowIfCancellationRequested();
        _chatGptSignIn = signIn;
        _bridge.Seed(DraftEndpointId, signIn.Account, signIn.Tokens);
        _expiresAt = null;
        SetAuthorizing(false);
        AuthorizationUrl = "";
        await DiscoverCoreAsync(ct);
    }

    /// <summary>
    /// Ends the renewable session of a sign-in that was never saved, so it doesn't linger in the
    /// user's ChatGPT account. Runs in the background; failure only leaves an unused session.
    /// </summary>
    private void AbandonChatGptSignIn(bool removeFromBridge)
    {
        if (_chatGptSignIn is not { } abandoned)
            return;
        _chatGptSignIn = null;
        var endpointId = DraftEndpointId;
        _ = Task.Run(async () =>
        {
            try
            {
                var refreshToken = (removeFromBridge ? await _bridge.RemoveAsync(endpointId, CancellationToken.None) : null)
                    ?? abandoned.Tokens.RefreshToken;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _chatGpt.RevokeAsync(abandoned.Account.ClientId, refreshToken, timeout.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EasyConnect] ChatGPT sign-in cleanup failed ({ex.GetType().Name}).");
            }
        });
    }

    private ByokEndpoint DraftEndpoint()
    {
        var headers = _reconnecting?.Headers is { } savedHeaders
            ? new Dictionary<string, string>(savedHeaders, StringComparer.OrdinalIgnoreCase) : [];
        if (IsFoundry)
            headers.TryAdd("api-key", ByokConfigHelper.ApiKeyHeaderToken);
        return new()
        {
            Id = DraftEndpointId,
            Name = _reconnecting?.Name ?? SelectedProvider?.Name ?? "",
            ConnectionProviderId = SelectedProvider?.Id,
            Account = _chatGptSignIn is { Account: var account }
                ? new ByokAccount { ClientId = account.ClientId, Subject = account.Subject, Email = account.Email }
                : _reconnecting?.Account?.Clone(),
            BaseUrl = IsFoundry ? ProviderConnectionService.NormalizeFoundryBaseUrl(BaseUrl.Trim()) : BaseUrl.Trim(),
            ProviderType = ProviderType,
            WireApi = WireApi,
            AzureApiVersion = ProviderType == "azure" ? AzureApiVersion : null,
            ApiKeyEnvVar = _reconnecting?.ApiKeyEnvVar,
            Headers = headers,
            IsEnabled = true,
            CredentialExpiresAt = _expiresAt
        };
    }

    private async Task<string?> CurrentKeyAsync()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
            return ApiKey.Trim();
        if (_pendingKey is not null)
            return _pendingKey;
        if (_reconnecting is { } endpoint)
        {
            if (endpoint.ApiKeyMode == ByokApiKeyMode.CredentialStore)
            {
                try
                {
                    return await _keys.GetAsync(SecureKeyStoreFactory.EndpointKey(endpoint.Id))
                           ?? ByokConfigHelper.ResolveApiKeyFromEnvironment(endpoint);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or TimeoutException)
                {
                    throw new ProviderConnectionException(Loc.Get("EasyConnect_StorageReadFailed"));
                }
            }
            return ByokConfigHelper.ResolveApiKey(endpoint, _keys);
        }
        return null;
    }

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (!CanDiscover)
            return;
        await RunAsync(DiscoverCoreAsync);
    }

    private async Task DiscoverCoreAsync(CancellationToken ct)
    {
        Status = Loc.Get("EasyConnect_FindingModels");
        IReadOnlyList<ProviderModel> models;
        string discoveryNotice = "";
        if (IsChatGpt)
            models = await DiscoverChatGptModelsAsync(ct);
        else
        {
            var key = HasKey ? await CurrentKeyAsync() : null;
            if (HasKey && string.IsNullOrEmpty(key))
                throw new ProviderConnectionException(Loc.Get("EasyConnect_KeyRequired"));
            try
            {
                models = await _connections.DiscoverAsync(DraftEndpoint(), key, ct);
            }
            catch (ProviderConnectionException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                models = [];
                discoveryNotice = IsFoundry ? Loc.Get("EasyConnect_FoundryNoModelDiscovery") : ex.Message;
                IsAddingManualModel = true;
            }
        }
        ct.ThrowIfCancellationRequested();
        if (IsFoundry && _reconnecting is { } foundry)
            models = models.Select(discovered =>
            {
                var configured = _store.Data.Settings.ByokModels.FirstOrDefault(model =>
                    model.EndpointId == foundry.Id && model.ModelId == discovered.Id);
                return discovered.SupportedReasoningEfforts is null && configured is { SupportsReasoningEffort: true }
                    ? discovered with
                    {
                        SupportedReasoningEfforts = configured.SupportedReasoningEfforts.ToArray(),
                        DefaultReasoningEffort = configured.DefaultReasoningEffort
                    }
                    : discovered;
            }).ToArray();
        // A metadata-only refresh updates configured models without reconnecting or changing the chosen models.
        if (_reconnecting is { } endpoint)
        {
            var changed = false;
            foreach (var savedModel in _store.Data.Settings.ByokModels.Where(model => model.EndpointId == endpoint.Id))
                if (models.FirstOrDefault(model => model.Id == savedModel.ModelId) is { } discovered)
                    changed |= ApplyDiscoveredReasoning(savedModel, IsFoundry
                        ? ProviderConnectionService.WithFoundryReasoning(discovered, WireApi) : discovered);
            if (changed)
            {
                await _store.SaveAsync();
                _saved();
            }
        }
        var previous = Models.Where(model => model.IsSelected).Select(model => model.SourceModel).ToList();
        var selectedIds = previous.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        // Every model a ChatGPT plan lists is included in it, so a new connection starts with all of them.
        var selectAll = IsChatGpt && !IsManaging && previous.Count == 0;
        Models.Clear();
        foreach (var model in models)
            AddChoice(model, selectAll || selectedIds.Contains(model.Id));
        // Keep chosen models a provider no longer lists (e.g. ones added by ID).
        foreach (var model in previous.Where(model => Models.All(choice => choice.Id != model.Id)))
            AddChoice(model, true);
        Status = discoveryNotice;
        Stage = ConnectStage.Models;
        NotifyModels();
    }

    private async Task<IReadOnlyList<ProviderModel>> DiscoverChatGptModelsAsync(CancellationToken ct)
    {
        if (_chatGptSignIn is null)
        {
            if (_reconnecting is not { Account: not null } saved || IsExpired(saved))
                throw new ProviderConnectionException(Loc.Get("ChatGpt_SignInRequired"));
            _bridge.Register(saved, _keys);
        }
        try
        {
            var token = await _bridge.GetAccessTokenAsync(DraftEndpointId, null, ct);
            var catalog = await _chatGpt.GetCatalogAsync(token, ct);
            _bridge.UseCatalog(DraftEndpointId, catalog);
            return catalog.Models;
        }
        catch (ChatGptRefreshException ex)
        {
            throw new ProviderConnectionException(ex.Message);
        }
    }

    [RelayCommand]
    private void ToggleManualModel()
    {
        IsAddingManualModel = !IsAddingManualModel;
        if (!IsAddingManualModel)
            ManualModel = "";
    }

    [RelayCommand]
    private void AddModel()
    {
        var id = ManualModel.Trim();
        if (string.IsNullOrEmpty(id))
        {
            Error = Loc.Get("EasyConnect_ModelRequired");
            return;
        }
        Error = "";
        var existing = Models.FirstOrDefault(model => model.Id == id);
        if (existing is not null)
            existing.IsSelected = true;
        else
            AddChoice(new(id, id), true);
        ManualModel = "";
        IsAddingManualModel = false;
        NotifyModels();
    }

    private void AddChoice(ProviderModel model, bool selected)
    {
        var choice = new ConnectModelChoice(model) { IsSelected = selected };
        if (IsFoundry)
            choice.UpdateFoundryReasoning(WireApi);
        choice.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ConnectModelChoice.IsSelected))
                UpdateActions();
        };
        Models.Add(choice);
    }

    internal static bool ApplyDiscoveredReasoning(ByokModel model, ProviderModel discovered)
    {
        if (discovered.SupportedReasoningEfforts is not { } efforts)
            return false;

        var preferredDefault = efforts.FirstOrDefault(effort =>
            string.Equals(effort, model.DefaultReasoningEffort, StringComparison.OrdinalIgnoreCase))
            ?? discovered.DefaultReasoningEffort;
        var supported = efforts.Count > 0;
        if (model.SupportsReasoningEffort == supported && model.SupportedReasoningEfforts.SequenceEqual(efforts)
            && model.DefaultReasoningEffort == preferredDefault)
            return false;

        model.SupportsReasoningEffort = supported;
        model.SupportedReasoningEfforts = efforts.ToList();
        model.DefaultReasoningEffort = preferredDefault;
        return true;
    }

    [RelayCommand]
    private async Task SaveConnectionAsync()
    {
        if (!CanSave || SelectedProvider is null)
            return;
        // Managing an account without entering a new key keeps the key it already has.
        var keepsSavedKey = KeepsSavedKey;
        if (HasKey && !keepsSavedKey && !CanSaveCredential)
        {
            Error = Loc.Get("EasyConnect_StorageConsentRequired");
            return;
        }
        var hadUsableDefault = HasUsableDefault(_reconnecting?.Id);
        var skipModelCheck = SkipModelCheck;
        await RunAsync(async ct =>
        {
            var endpoint = DraftEndpoint();
            ProviderConnectionService.BuildModelsUri(endpoint);
            var isChatGpt = IsChatGpt;
            var key = HasKey && !isChatGpt ? await CurrentKeyAsync() : null;
            if (HasKey && !isChatGpt && string.IsNullOrEmpty(key))
                throw new ProviderConnectionException(Loc.Get("EasyConnect_KeyRequired"));
            var selected = Models.Where(model => model.IsSelected).ToArray();
            var starter = selected[0];
            Stage = ConnectStage.Connecting;
            IsSavingStage = false;
            ProviderConfig provider;
            if (isChatGpt)
            {
                if (endpoint.Account is null)
                    throw new ProviderConnectionException(Loc.Get("ChatGpt_SignInRequired"));
                // A fresh sign-in is served from memory until it is saved; a saved one reads its stored token.
                endpoint.ApiKeyMode = _chatGptSignIn is null ? ByokApiKeyMode.CredentialStore : ByokApiKeyMode.None;
                endpoint.ApiKey = null;
                endpoint.CredentialExpiresAt = null;
                provider = _bridge.CreateProviderConfig(endpoint, _keys);
            }
            else
            {
                endpoint.ApiKeyMode = HasKey ? ByokApiKeyMode.Stored : ByokApiKeyMode.None;
                endpoint.ApiKey = key;
                provider = ByokConfigHelper.BuildProviderConfig(endpoint);
            }
            if (!skipModelCheck)
            {
                var called = false;
                var tool = AIFunctionFactory.Create(() =>
                {
                    called = true;
                    return "LUMI_CONNECTION_OK";
                }, "lumi_connection_check", "Read-only connection check. Returns the verification word.");
                if (isChatGpt)
                    _bridge.TakeFailure(endpoint.Id);
                try
                {
                    await _copilot.UseLightweightSessionAsync(
                        new LightweightSessionOptions
                        {
                            Model = starter.Id,
                            Provider = provider,
                            Streaming = true,
                            SystemPrompt = "Call lumi_connection_check once, then respond with its exact returned word. No other action.",
                            Tools = [tool],
                            ModelCapabilities = starter.Model.ContextTokens is { } tokens
                                ? new GitHub.Copilot.Rpc.ModelCapabilitiesOverride
                                { Limits = new GitHub.Copilot.Rpc.ModelCapabilitiesOverrideLimits { MaxContextWindowTokens = tokens } } : null
                        },
                        async (session, innerCt) =>
                        {
                            var result = await session.SendAndWaitAsync(
                                new MessageOptions { Prompt = "Verify the connection by calling lumi_connection_check." },
                                TimeSpan.FromSeconds(90), innerCt);
                            if (!called || result?.Data.Content?.Contains("LUMI_CONNECTION_OK", StringComparison.Ordinal) != true)
                                throw new ProviderConnectionException(Loc.Get("EasyConnect_ToolCheckFailed"));
                        }, ct);
                }
                // The bridge words ChatGPT's own refusals, so the user learns why instead of a generic failure.
                catch (Exception ex) when (isChatGpt && ex is not OperationCanceledException
                                           && _bridge.TakeFailure(endpoint.Id) is { } refusal)
                {
                    throw new ProviderConnectionException(refusal);
                }
            }
            ct.ThrowIfCancellationRequested();
            _isCommitting = true;
            OnPropertyChanged(nameof(CanCancel));
            IsSavingStage = true;

            if (isChatGpt)
            {
                if (_chatGptSignIn is not null)
                {
                    try { await _bridge.PersistAsync(endpoint.Id, _keys, ct); }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or TimeoutException)
                    {
                        throw new ProviderConnectionException(Loc.Get("EasyConnect_StorageFailed"));
                    }
                }
                endpoint.ApiKeyMode = ByokApiKeyMode.CredentialStore;
            }
            else if (keepsSavedKey && _reconnecting is { } original)
                PreserveSavedKey(endpoint, original);
            else if (_keys.IsSupported && (HasKey || _reconnecting is { ApiKeyMode: ByokApiKeyMode.CredentialStore }))
            {
                // A null key removes the saved credential when authentication is turned off.
                try { await _keys.SetAsync(SecureKeyStoreFactory.EndpointKey(endpoint.Id), key); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException or TimeoutException)
                {
                    throw new ProviderConnectionException(Loc.Get("EasyConnect_StorageFailed"));
                }
                endpoint.ApiKeyMode = HasKey ? ByokApiKeyMode.CredentialStore : ByokApiKeyMode.None;
                endpoint.ApiKey = null;
            }
            var settings = _store.Data.Settings;
            var previousEndpoint = settings.ByokEndpoints.FirstOrDefault(item => item.Id == endpoint.Id);
            var endpointIndex = previousEndpoint is null ? -1 : settings.ByokEndpoints.IndexOf(previousEndpoint);
            if (endpointIndex >= 0)
                settings.ByokEndpoints[endpointIndex] = endpoint;
            else
                settings.ByokEndpoints.Add(endpoint);
            foreach (var choice in selected)
            {
                var model = settings.ByokModels.FirstOrDefault(item => item.EndpointId == endpoint.Id && item.ModelId == choice.Id);
                if (model is null)
                {
                    model = new ByokModel
                    {
                        EndpointId = endpoint.Id, ModelId = choice.Id,
                        DisplayName = choice.Name,
                        DefaultContextWindowTokens = choice.Model.ContextTokens
                    };
                    settings.ByokModels.Add(model);
                }
                ApplyDiscoveredReasoning(model, choice.Model);
                model.IsEnabled = true;
            }
            foreach (var model in settings.ByokModels.Where(model => model.EndpointId == endpoint.Id))
                model.IsEnabled = selected.Any(choice => choice.Id == model.ModelId);

            var starterToken = ByokConfigHelper.BuildModelToken(settings.ByokModels.First(item =>
                item.EndpointId == endpoint.Id && item.ModelId == starter.Id));
            var keepsCurrentDefault = settings.ByokModels.Any(model => model.EndpointId == endpoint.Id && model.IsEnabled
                && ByokConfigHelper.BuildModelToken(model) == settings.PreferredModel);
            var lostDefault = settings.ByokModels.Any(model => model.EndpointId == endpoint.Id && !model.IsEnabled
                && ByokConfigHelper.BuildModelToken(model) == settings.PreferredModel);
            var becameDefault = !keepsCurrentDefault && (MakeDefault || !hadUsableDefault || lostDefault);
            if (becameDefault)
                settings.PreferredModel = starterToken;
            await _store.SaveAsync();
            _saved();
            ApiKey = "";
            _pendingKey = null;
            var defaultChoice = selected.FirstOrDefault(choice => settings.ByokModels.Any(model =>
                model.EndpointId == endpoint.Id && model.ModelId == choice.Id
                && ByokConfigHelper.BuildModelToken(model) == settings.PreferredModel));
            DoneText = defaultChoice is not null
                ? string.Format(Loc.Get(skipModelCheck ? "EasyConnect_DoneUnverifiedDefault" : "EasyConnect_DoneDefault"), defaultChoice.Name)
                : string.Format(Loc.Get(skipModelCheck ? "EasyConnect_DoneUnverifiedAvailable" : "EasyConnect_DoneAvailable"), starter.Name);
            if (isChatGpt && !IsManaging)
                DoneText += " " + Loc.Get("ChatGpt_DoneUsage");
            _chatGptSignIn = null;
            Stage = ConnectStage.Done;
            RefreshOverview(force: true);
        });
        if (Stage == ConnectStage.Connecting)
            Stage = ConnectStage.Models;
    }

    // ═══ Disconnecting ═══

    /// <summary>
    /// Keeps how an account already resolves its key (an environment variable stays an environment
    /// variable, a stored key stays where it is) when it was re-verified without a new key.
    /// </summary>
    internal static void PreserveSavedKey(ByokEndpoint endpoint, ByokEndpoint original)
    {
        endpoint.ApiKeyMode = original.ApiKeyMode;
        endpoint.ApiKey = original.ApiKeyMode == ByokApiKeyMode.Stored ? original.ApiKey : null;
        endpoint.ApiKeyEnvVar = original.ApiKeyEnvVar;
        endpoint.CredentialExpiresAt = original.CredentialExpiresAt;
    }

    [RelayCommand]
    private void RequestRemove() => IsConfirmingRemove = true;

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        if (_reconnecting is not { } endpoint)
            return;
        await DisconnectAsync(endpoint);
        // A disconnected account closes its dialog; any note about it stays visible in the list.
        if (!HasError || !_store.Data.Settings.ByokEndpoints.Contains(endpoint))
        {
            var note = Error;
            IsDialogOpen = false;
            Error = note;
        }
    }

    private async Task DisconnectAsync(ByokEndpoint target)
    {
        if (IsWorking)
            return;
        await RunAsync(async ct =>
        {
            var revocationUnconfirmed = ByokConfigHelper.IsChatGptEndpoint(target)
                && !await EndChatGptSessionAsync(target, ct);
            if (target.ApiKeyMode == ByokApiKeyMode.CredentialStore)
                await _keys.DeleteAsync(SecureKeyStoreFactory.EndpointKey(target.Id));
            var settings = _store.Data.Settings;
            var removed = settings.ByokModels.Where(model => model.EndpointId == target.Id).ToArray();
            settings.ByokModels.RemoveAll(model => model.EndpointId == target.Id);
            settings.ByokEndpoints.Remove(target);
            if (removed.Any(model => ByokConfigHelper.BuildModelToken(model) == settings.PreferredModel))
            {
                // GitHub Copilot takes the default back when it is available.
                settings.PreferredModel = (IsGitHubSignedIn && !settings.UseBYOKOnly ? PickCopilotDefault() : null)
                    ?? (GetUsableModels().FirstOrDefault() is { } next
                        ? ByokConfigHelper.BuildModelToken(next) : "");
            }
            await _store.SaveAsync();
            _saved();
            RefreshOverview(force: true);
            if (revocationUnconfirmed)
                Error = Loc.Get("ChatGpt_RevokeUnconfirmed");
        });
    }

    /// <summary>
    /// Stops serving a ChatGPT account and revokes its renewable session. Returns false when ChatGPT
    /// could not confirm it; the user can still remove Lumi in ChatGPT's settings.
    /// </summary>
    private async Task<bool> EndChatGptSessionAsync(ByokEndpoint target, CancellationToken ct)
    {
        var refreshToken = await _bridge.RemoveAsync(target.Id, ct);
        if (refreshToken is null && target.ApiKeyMode == ByokApiKeyMode.CredentialStore)
            refreshToken = await _keys.GetAsync(SecureKeyStoreFactory.EndpointKey(target.Id));
        if (refreshToken is null || target.Account is not { ClientId.Length: > 0 } account)
            return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await _chatGpt.RevokeAsync(account.ClientId, refreshToken, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsWorking || _disposed)
            return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        IsWorking = true;
        Error = "";
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { Status = Loc.Get("EasyConnect_Cancelled"); AuthorizationUrl = ""; }
        catch (OperationCanceledException) { Error = Loc.Get("EasyConnect_Timeout"); Status = ""; AuthorizationUrl = ""; }
        catch (ProviderOAuthException ex) { Error = ex.Message; Status = ""; AuthorizationUrl = ""; }
        catch (ProviderConnectionException ex) { Error = ex.Message; Status = ""; }
        catch (HttpRequestException)
        {
            Error = IsLocal ? string.Format(Loc.Get("EasyConnect_LocalNotRunning"), SelectedName) : Loc.Get("EasyConnect_NetworkFailed");
            Status = "";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EasyConnect] Connection failed ({ex.GetType().Name}).");
            Error = ProviderConnectionService.DescribeFailure(ex);
            Status = "";
        }
        finally
        {
            _operation = null;
            _isCommitting = false;
            IsWorking = false;
            if (!IsDialogOpen && Stage != ConnectStage.Gallery)
                ResetWizard();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _store.IndexSaved -= OnIndexSaved;
        _bridge.SignInRequired -= OnChatGptSignInRequired;
        if (_loginVM is not null)
            _loginVM.PropertyChanged -= OnLoginPropertyChanged;
        _operation?.Cancel();
        _connections.Dispose();
        _oauth.Dispose();
        if (_ownsChatGpt)
            _chatGpt.Dispose();
    }
}
