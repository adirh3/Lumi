using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.ImageGeneration;

namespace Lumi.ViewModels;

/// <summary>Where chats create images: GitHub Copilot, one of the user's accounts, or automatic.</summary>
public sealed record ImageProviderOption(string? Id, string Title, string Detail);

/// <summary>An image model an account offers; a null id lets Lumi pick the newest.</summary>
public sealed record ImageModelOption(string? Id, string Title);

/// <summary>
/// Settings → AI &amp; Models → Image generation. Chats create images with GitHub Copilot's image model while
/// Copilot can run, and otherwise with an account of the user's that speaks the OpenAI Images API
/// (<see cref="ImageGenerationRouting"/>). The page says which one chats use now, and offers to connect an
/// account when none can.
/// </summary>
public partial class SettingsViewModel
{
    private CancellationTokenSource? _imageModelDiscovery;
    private string? _imageModelsAccountId;
    private bool _syncingImageGeneration;

    [ObservableProperty] private bool _enableImageGeneration;
    [ObservableProperty] private bool _imageGenerationWatermark;
    [ObservableProperty] private ImageProviderOption? _selectedImageProvider;
    [ObservableProperty] private ImageModelOption? _selectedImageModel;
    [ObservableProperty] private string? _imageModelStatus;

    public ObservableCollection<ImageProviderOption> ImageProviderOptions { get; } = [];
    public ObservableCollection<ImageModelOption> ImageModelOptions { get; } = [];

    private ImageGenerationPlan ImagePlan
        => ImageGenerationRouting.Resolve(_dataStore.Data.Settings, _copilotService.CanUseCopilotImages);

    /// <summary>What creates images in chats now, in a sentence.</summary>
    public string ImageGenerationStatus
    {
        get
        {
            var settings = _dataStore.Data.Settings;
            if (!settings.EnableImageGeneration)
                return Loc.Get("ImageGen_StatusOff");

            var plan = ImagePlan;
            return plan.Route switch
            {
                ImageGenerationRoute.Copilot => Loc.Get("ImageGen_StatusCopilot",
                    _copilotService.CopilotImageModel?.Name ?? Loc.Get("ImageGen_CopilotModelFallback")),
                ImageGenerationRoute.Account => Loc.Get("ImageGen_StatusAccount", plan.Account!.Name,
                    ImageGenerationRouting.ModelFor(settings, plan.Account) ?? Loc.Get("ImageGen_NewestModel")),
                _ when settings.UseBYOKOnly => Loc.Get("ImageGen_UnavailableOwnAccounts"),
                _ when !_copilotService.IsGitHubAuthenticated => Loc.Get("ImageGen_UnavailableSignedOut"),
                _ => Loc.Get("ImageGen_UnavailablePlan"),
            };
        }
    }

    public bool IsImageGenerationReady => ImagePlan.Route != ImageGenerationRoute.None;
    public bool IsImageGenerationUnavailable => EnableImageGeneration && !IsImageGenerationReady;
    /// <summary>The picker is a choice only when an account can create images too.</summary>
    public bool ShowsImageProvider => EnableImageGeneration && ImageGenerationRouting.ImageAccounts(_dataStore.Data.Settings).Count > 0;
    public bool ShowsImageModel => EnableImageGeneration && ImagePlan.Route == ImageGenerationRoute.Account;
    public bool ShowsImageWatermark => EnableImageGeneration && ImagePlan.Route == ImageGenerationRoute.Copilot;
    public bool IsEnableImageGenerationModified => EnableImageGeneration != _defaults.EnableImageGeneration;

    private void InitializeImageGeneration(UserSettings settings)
    {
        _enableImageGeneration = settings.EnableImageGeneration;
        _imageGenerationWatermark = settings.ImageGenerationWatermark;
        ByokEndpoints.CollectionChanged += OnImageAccountsChanged;
        _copilotService.ModelCatalogChanged += OnImageCatalogChanged;
        SyncImageGenerationOptions();
    }

    private void DisposeImageGeneration()
    {
        ByokEndpoints.CollectionChanged -= OnImageAccountsChanged;
        _copilotService.ModelCatalogChanged -= OnImageCatalogChanged;
        _imageModelDiscovery?.Cancel();
    }

    private void OnImageAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncImageGenerationOptions();

    private void OnImageCatalogChanged(ModelCatalogSnapshot snapshot)
        => Dispatcher.UIThread.Post(SyncImageGenerationOptions);

    partial void OnEnableImageGenerationChanged(bool value)
    {
        _dataStore.Data.Settings.EnableImageGeneration = value;
        Save();
        NotifyModified();
        RefreshImageGenerationState();
    }

    partial void OnImageGenerationWatermarkChanged(bool value)
    {
        _dataStore.Data.Settings.ImageGenerationWatermark = value;
        Save();
    }

    partial void OnSelectedImageProviderChanged(ImageProviderOption? value)
    {
        if (_syncingImageGeneration || value is null)
            return;

        var settings = _dataStore.Data.Settings;
        if (settings.ImageGenerationProvider == value.Id)
            return;

        settings.ImageGenerationProvider = value.Id;
        // A model belongs to the account it was chosen for.
        settings.ImageGenerationModel = null;
        Save();
        SyncImageModelOptions(discovered: null);
        RefreshImageGenerationState();
    }

    partial void OnSelectedImageModelChanged(ImageModelOption? value)
    {
        if (_syncingImageGeneration || value is null || ImagePlan.Account is not { } account)
            return;

        var settings = _dataStore.Data.Settings;
        if (ImageGenerationRouting.ModelFor(settings, account) == value.Id)
            return;

        // A model belongs to the account it was chosen for, so choosing one picks that account too.
        settings.ImageGenerationProvider = account.Id;
        settings.ImageGenerationModel = value.Id;
        AccountImageGenerationTool.ForgetDiscoveredModel(account.Id);
        Save();
        _syncingImageGeneration = true;
        SelectedImageProvider = ImageProviderOptions.FirstOrDefault(option => option.Id == account.Id) ?? SelectedImageProvider;
        _syncingImageGeneration = false;
        RefreshImageGenerationState();
    }

    [RelayCommand]
    private void RevertEnableImageGeneration() => EnableImageGeneration = _defaults.EnableImageGeneration;

    /// <summary>Connects an account for images: the connect dialog, at the providers that create them.</summary>
    [RelayCommand]
    private void ConnectImageAccount() => EasyConnect.OpenImageSetupCommand.Execute(null);

    /// <summary>Why to add one: chats have nothing to create images with yet, or they could use your own account.</summary>
    public string ImageAccountHint => Loc.Get(IsImageGenerationUnavailable ? "ImageGen_ConnectDescNeeded" : "ImageGen_ConnectDesc");

    /// <summary>Reads the image models the chosen account offers, for its model picker.</summary>
    [RelayCommand]
    private async Task RefreshImageModelsAsync()
    {
        if (ImagePlan is not { Route: ImageGenerationRoute.Account, Account: { } account })
            return;

        _imageModelDiscovery?.Cancel();
        var discovery = _imageModelDiscovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        ImageModelStatus = null;
        try
        {
            var connection = await Task.Run(() => ImageAccountConnection.From(account, _secureKeyStore ?? Lumi.Services.Byok.SecureKeyStoreFactory.Instance));
            var models = await OpenAiImageClient.Shared.ListImageModelsAsync(connection, discovery.Token);
            if (discovery.IsCancellationRequested)
                return;
            SyncImageModelOptions(models);
            ImageModelStatus = models.Count == 0 ? Loc.Get("ImageGen_NoModelsListed", account.Name) : null;
        }
        catch (OperationCanceledException) when (discovery.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is ImageAccountException or System.Net.Http.HttpRequestException or InvalidOperationException
                                          or OperationCanceledException)
        {
            ImageModelStatus = Loc.Get("ImageGen_ModelsUnavailable", error.Message);
        }
    }

    private void ResetImageGeneration(UserSettings defaults)
    {
        var settings = _dataStore.Data.Settings;
        settings.ImageGenerationProvider = defaults.ImageGenerationProvider;
        settings.ImageGenerationModel = defaults.ImageGenerationModel;
        EnableImageGeneration = defaults.EnableImageGeneration;
        ImageGenerationWatermark = defaults.ImageGenerationWatermark;
        SyncImageGenerationOptions();
    }

    /// <summary>Lists the providers that can create images now and selects the saved one.</summary>
    private void SyncImageGenerationOptions()
    {
        var settings = _dataStore.Data.Settings;
        // Connecting an account for images can turn image generation on outside this page.
        if (_enableImageGeneration != settings.EnableImageGeneration)
        {
            _enableImageGeneration = settings.EnableImageGeneration;
            OnPropertyChanged(nameof(EnableImageGeneration));
        }

        _syncingImageGeneration = true;
        try
        {
            var options = new List<ImageProviderOption>
            {
                new(null, Loc.Get("ImageGen_ProviderAutomatic"), Loc.Get("ImageGen_ProviderAutomaticDetail")),
            };
            if (_copilotService.CanUseCopilotImages && !settings.UseBYOKOnly)
                options.Add(new(UserSettings.CopilotImageProvider, Loc.Get("ImageGen_ProviderCopilot"),
                    _copilotService.CopilotImageModel?.Name ?? Loc.Get("ImageGen_CopilotModelFallback")));
            foreach (var account in ImageGenerationRouting.ImageAccounts(settings))
                options.Add(new(account.Id, account.Name, Loc.Get("ImageGen_ProviderAccountDetail")));

            // Replace in place only when the list really changed, so an open picker isn't rebuilt under the pointer.
            if (!options.SequenceEqual(ImageProviderOptions))
            {
                ImageProviderOptions.Clear();
                foreach (var option in options)
                    ImageProviderOptions.Add(option);
            }

            SelectedImageProvider = ImageProviderOptions.FirstOrDefault(option => option.Id == settings.ImageGenerationProvider)
                                    ?? ImageProviderOptions[0];
            SyncImageModelOptions(discovered: null);
        }
        finally
        {
            _syncingImageGeneration = false;
        }

        RefreshImageGenerationState();
    }

    /// <summary>The image models listed for the account chats use, keeping a list only while it is that account's.</summary>
    private void SyncImageModelOptions(IReadOnlyList<string>? discovered)
    {
        var account = ImagePlan.Account;
        var saved = account is null ? null : ImageGenerationRouting.ModelFor(_dataStore.Data.Settings, account);
        var listed = discovered
            ?? (account?.Id == _imageModelsAccountId
                ? ImageModelOptions.Where(static option => option.Id is not null).Select(static option => option.Id!).ToList()
                : []);
        _imageModelsAccountId = account?.Id;
        var ids = listed
            .Append(saved)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var wasSyncing = _syncingImageGeneration;
        _syncingImageGeneration = true;
        try
        {
            ImageModelOptions.Clear();
            ImageModelOptions.Add(new(null, Loc.Get("ImageGen_ModelNewest")));
            foreach (var id in ids)
                ImageModelOptions.Add(new(id, id));
            SelectedImageModel = ImageModelOptions.FirstOrDefault(option => option.Id == saved) ?? ImageModelOptions[0];
        }
        finally
        {
            _syncingImageGeneration = wasSyncing;
        }
    }

    private void RefreshImageGenerationState()
    {
        OnPropertyChanged(nameof(ImageGenerationStatus));
        OnPropertyChanged(nameof(IsImageGenerationReady));
        OnPropertyChanged(nameof(IsImageGenerationUnavailable));
        OnPropertyChanged(nameof(ImageAccountHint));
        OnPropertyChanged(nameof(ShowsImageProvider));
        OnPropertyChanged(nameof(ShowsImageModel));
        OnPropertyChanged(nameof(ShowsImageWatermark));
        OnPropertyChanged(nameof(IsEnableImageGenerationModified));
    }
}
