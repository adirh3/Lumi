using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.ImageGeneration;

namespace Lumi.ViewModels;

/// <summary>An image model, or on Microsoft Foundry an image deployment, the account can create images with.</summary>
public partial class ImageModelChoice(string id) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Images in the connect dialog. Choosing an account's models also chooses whether chats create images with it, and
/// with which of its image models (on Foundry, which of its deployments, read from the resource itself). Opened from
/// Settings → Image generation, the dialog lists only the providers that create images and asks only about images,
/// so an account can be connected for images alone.
/// </summary>
public partial class EasyConnectViewModel
{
    private const int ShownImageModels = 8;
    private int _imageDiscovery;
    private OpenAiImageClient? _imageClient;

    // Through the same client as the account's other requests (a test's stub included).
    private OpenAiImageClient ImageClient => _imageClient ??= new OpenAiImageClient(_connections.Http);

    /// <summary>Opened to add an account for images: the gallery lists image providers, the model step asks only about images.</summary>
    [ObservableProperty] private bool _isImageSetup;
    [ObservableProperty] private bool _useForImages;
    [ObservableProperty] private bool _isFindingImageModels;
    [ObservableProperty] private bool _isAddingImageModel;
    [ObservableProperty] private string _manualImageModel = "";

    public ObservableCollection<ImageModelChoice> ImageModels { get; } = [];

    public bool CanCreateImages => SelectedProvider is { } provider && ImageGenerationRouting.IsImageProvider(provider.Id);
    public bool ShowImageSection => IsModelStage && CanCreateImages;
    public bool ShowChatModels => !IsImageSetup;
    public bool ShowImageToggle => !IsImageSetup;
    public bool ShowImageChoices => UseForImages && !IsFindingImageModels;
    public bool ShowNoImageModels => ShowImageChoices && ImageModels.Count == 0;
    public bool ShowImageModelEntry => ShowImageChoices && (IsAddingImageModel || ImageModels.Count == 0);
    public bool ShowAddImageModel => ShowImageChoices && !ShowImageModelEntry;
    public string? SelectedImageModelId => ImageModels.FirstOrDefault(static model => model.IsSelected)?.Id;
    public bool ImagesReady => UseForImages && SelectedImageModelId is not null;
    public string ImageSectionCaption => Loc.Get(IsFoundry ? "EasyConnect_ImagesFoundryCaption" : "EasyConnect_ImagesCaption");
    public string NoImageModelsText => Loc.Get(IsFoundry ? "EasyConnect_NoImageDeployments" : "EasyConnect_NoImageModels");
    public string ImageModelPlaceholder => Loc.Get(IsFoundry ? "EasyConnect_ImageDeploymentPlaceholder" : "EasyConnect_ImageModelPlaceholder");
    public string AddImageModelText => Loc.Get(IsFoundry ? "EasyConnect_AddImageDeployment" : "EasyConnect_AddImageModel");

    /// <summary>Settings → Image generation: connect an account for images, starting from the providers that create them.</summary>
    [RelayCommand]
    private void OpenImageSetup()
    {
        if (IsWorking)
        {
            IsDialogOpen = true;
            return;
        }
        ResetWizard();
        IsImageSetup = true;
        RefreshProviders();
        IsDialogOpen = true;
    }

    [RelayCommand]
    private void ToggleImageModelEntry()
    {
        IsAddingImageModel = !IsAddingImageModel;
        ManualImageModel = "";
    }

    [RelayCommand]
    private void AddImageModel()
    {
        var id = ManualImageModel.Trim();
        if (id.Length == 0)
            return;
        var choice = ImageModels.FirstOrDefault(model => model.Id == id) ?? AddImageChoice(id);
        SelectImageModel(choice);
        ManualImageModel = "";
        IsAddingImageModel = false;
        NotifyImages();
    }

    partial void OnIsImageSetupChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowChatModels));
        OnPropertyChanged(nameof(ShowImageToggle));
        OnPropertyChanged(nameof(DialogTitle));
        NotifyImages();
    }

    partial void OnUseForImagesChanged(bool value) => NotifyImages();
    partial void OnIsFindingImageModelsChanged(bool value) => NotifyImages();
    partial void OnIsAddingImageModelChanged(bool value) => NotifyImages();

    private void ResetImages()
    {
        _imageDiscovery++;
        ImageModels.Clear();
        UseForImages = false;
        IsFindingImageModels = false;
        IsAddingImageModel = false;
        ManualImageModel = "";
    }

    /// <summary>
    /// Reads the account's image models as the model step opens. An account set up for images keeps its model;
    /// otherwise images are offered on when chats would have none without it, or when the dialog was opened for them.
    /// </summary>
    private void BeginImageDiscovery(ByokEndpoint endpoint, Func<string?> key)
    {
        if (!CanCreateImages)
        {
            ResetImages();
            return;
        }

        var settings = _store.Data.Settings;
        var savedModel = ImageGenerationRouting.ModelFor(settings, endpoint);
        UseForImages = IsImageSetup || settings.ImageGenerationProvider == endpoint.Id
                       || !_copilot.CanUseCopilotImages || settings.UseBYOKOnly;
        // The saved model is chosen at once, so saving before the list arrives keeps it.
        ImageModels.Clear();
        if (savedModel is not null)
            SelectImageModel(AddImageChoice(savedModel));
        NotifyImages();
        _ = DiscoverImageModelsAsync(endpoint, key);
    }

    private async Task DiscoverImageModelsAsync(ByokEndpoint endpoint, Func<string?> key)
    {
        var generation = ++_imageDiscovery;
        IsFindingImageModels = true;
        IReadOnlyList<string> models = [];
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var connection = await Task.Run(() => ImageAccountConnection.From(endpoint, key()), timeout.Token);
            models = await ImageClient.ListImageModelsAsync(connection, timeout.Token);
        }
        catch (Exception error) when (error is ImageAccountException or HttpRequestException or OperationCanceledException
                                          or InvalidOperationException)
        {
            // Without a list the user names the model; the account's own error shows if it then fails.
        }

        if (generation != _imageDiscovery)
            return;

        // Whatever is chosen meanwhile (the saved model, or one entered by hand) stays chosen and listed.
        var chosen = SelectedImageModelId;
        ImageModels.Clear();
        foreach (var id in models.Take(ShownImageModels))
            AddImageChoice(id);
        if (chosen is not null && ImageModels.All(model => model.Id != chosen))
            AddImageChoice(chosen);
        if ((ImageModels.FirstOrDefault(model => model.Id == chosen) ?? ImageModels.FirstOrDefault()) is { } selected)
            SelectImageModel(selected);
        IsFindingImageModels = false;
        NotifyImages();
    }

    private ImageModelChoice AddImageChoice(string id)
    {
        var choice = new ImageModelChoice(id);
        choice.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ImageModelChoice.IsSelected))
                return;
            // One image model at a time: picking one sets it, and the picked one can't be cleared.
            if (choice.IsSelected)
                SelectImageModel(choice);
            else if (ImageModels.All(model => !model.IsSelected))
                choice.IsSelected = true;
            NotifyImages();
        };
        ImageModels.Add(choice);
        return choice;
    }

    private void SelectImageModel(ImageModelChoice choice)
    {
        // Selected first, so clearing the others never leaves none selected.
        choice.IsSelected = true;
        foreach (var model in ImageModels)
        {
            if (!ReferenceEquals(model, choice) && model.IsSelected)
                model.IsSelected = false;
        }
    }

    /// <summary>Sets where chats create images as the account is saved: with it, or no longer with it.</summary>
    private string? ApplyImageChoice(UserSettings settings, string endpointId)
    {
        if (ImagesReady && CanCreateImages)
        {
            settings.EnableImageGeneration = true;
            settings.ImageGenerationProvider = endpointId;
            settings.ImageGenerationModel = SelectedImageModelId;
            AccountImageGenerationTool.ForgetDiscoveredModel(endpointId);
            return SelectedImageModelId;
        }

        if (settings.ImageGenerationProvider == endpointId)
        {
            settings.ImageGenerationProvider = null;
            settings.ImageGenerationModel = null;
        }
        return null;
    }

    private void NotifyImages()
    {
        foreach (var name in new[] { nameof(CanCreateImages), nameof(ShowImageSection), nameof(ShowImageChoices),
                     nameof(ShowNoImageModels), nameof(ShowImageModelEntry), nameof(ShowAddImageModel),
                     nameof(SelectedImageModelId), nameof(ImagesReady), nameof(ImageSectionCaption),
                     nameof(NoImageModelsText), nameof(ImageModelPlaceholder), nameof(AddImageModelText),
                     nameof(ShowsModelCheck) })
            OnPropertyChanged(name);
        UpdateActions();
    }
}
