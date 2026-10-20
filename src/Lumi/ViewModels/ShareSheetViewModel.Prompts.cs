using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services.Sharing;

namespace Lumi.ViewModels;

public partial class ShareSheetViewModel
{
    private CancellationTokenSource? _promptPreparation;
    private PromptShareSource? _promptSource;
    private Func<PromptShareSource, CancellationToken, Task<SharedPrompt>>? _preparePrompt;
    private IReadOnlyList<PromptInput> _promptInputDefinitions = [];
    private bool _updatingPrompt;

    [ObservableProperty] private bool _isPrompt;
    [ObservableProperty] private bool _isPreparingPrompt;
    [ObservableProperty] private string _promptName = "";
    [ObservableProperty] private string _promptDescription = "";
    [ObservableProperty] private string _promptContent = "";
    [ObservableProperty] private string _promptRequirements = "";
    [ObservableProperty] private string _promptSourceDescription = "";

    public bool CanShare => _share is not null && !IsPreparingPrompt;
    public IReadOnlyList<PromptInput> PromptInputs => _share?.Pack.Prompt?.Inputs ?? [];
    public bool HasPromptInputs => PromptInputs.Count > 0;

    partial void OnIsOpenChanged(bool value)
    {
        if (!value)
        {
            CancelPromptPreparation();
            _promptSource = null;
            _preparePrompt = null;
        }
    }

    partial void OnIsPreparingPromptChanged(bool value) => NotifyShareAvailability();
    partial void OnPromptNameChanged(string value) => RefreshEditedPrompt();
    partial void OnPromptDescriptionChanged(string value) => RefreshEditedPrompt();
    partial void OnPromptContentChanged(string value) => RefreshEditedPrompt();
    partial void OnPromptRequirementsChanged(string value) => RefreshEditedPrompt();

    public Task OpenForPromptAsync(PromptShareSource source, Func<PromptShareSource, CancellationToken, Task<SharedPrompt>> prepare)
    {
        CancelPromptPreparation();
        _promptSource = source;
        _preparePrompt = prepare;
        _share = null;
        IsPrompt = true;
        IsSkill = false;
        IsMcpServer = false;
        HasUnsavedEdits = false;
        Title = Loc.Prompt_ShareTitle;
        Card = null;
        Contents.Clear();
        PrivacyNotes.Clear();
        Targets.Clear();
        IsChatCodeLong = false;
        IsPreviewExpanded = false;
        CopyTextLabel = Loc.Prompt_CopyText;
        InteropText = Loc.Prompt_Interop;
        PromptSourceDescription = string.Format(CultureInfo.CurrentCulture, Loc.Prompt_Source, source.MessageCount)
            + (source.WasShortened ? " " + Loc.Prompt_SourceShortened : "");
        _updatingPrompt = true;
        PromptName = "";
        PromptDescription = "";
        PromptContent = "";
        PromptRequirements = "";
        _updatingPrompt = false;
        IsOpen = true;
        return PreparePromptAsync();
    }

    [RelayCommand]
    private Task PreparePromptAgain() => PreparePromptAsync();

    private async Task PreparePromptAsync()
    {
        if (_promptSource is not { } source || _preparePrompt is not { } prepare)
            return;
        CancelPromptPreparation();
        var cancellation = new CancellationTokenSource();
        _promptPreparation = cancellation;
        IsPreparingPrompt = true;
        SetStatus(Loc.Prompt_Preparing);
        try
        {
            var prompt = PromptSharing.Normalize(await prepare(source, cancellation.Token));
            if (!IsOpen || !ReferenceEquals(_promptPreparation, cancellation))
                return;
            _promptInputDefinitions = prompt.Inputs;
            _updatingPrompt = true;
            PromptName = prompt.Name;
            PromptDescription = prompt.Description;
            PromptContent = prompt.Content;
            PromptRequirements = string.Join('\n', prompt.Requirements);
            _updatingPrompt = false;
            RefreshEditedPrompt();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException
                                   or TimeoutException or IOException or HttpRequestException
                                   or StreamJsonRpc.RemoteInvocationException)
        {
            Debug.WriteLine($"[Lumi] Prompt drafting failed: {ex.Message}");
            if (IsOpen && ReferenceEquals(_promptPreparation, cancellation))
            {
                SetStatus(string.Format(CultureInfo.CurrentCulture, Loc.Prompt_PreparationFailed, ex.Message), isError: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_promptPreparation, cancellation))
            {
                _promptPreparation = null;
                IsPreparingPrompt = false;
            }
            cancellation.Dispose();
        }
    }

    private void CancelPromptPreparation()
    {
        var cancellation = _promptPreparation;
        _promptPreparation = null;
        cancellation?.Cancel();
        IsPreparingPrompt = false;
    }

    private void RefreshEditedPrompt()
    {
        if (_updatingPrompt || !IsPrompt)
            return;
        try
        {
            var prompt = PromptSharing.Normalize(new SharedPrompt
            {
                Name = PromptName,
                Description = PromptDescription,
                Content = PromptContent,
                Inputs = PromptSharing.InputsForEditedContent(PromptContent, _promptInputDefinitions),
                Requirements = PromptRequirements.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            });
            if (PromptDraftBuilder.HasCredentialText(prompt))
                throw new ArgumentException(Loc.Prompt_SecretWarning);
            _share = CapabilityPackWriter.ForPrompt(prompt);
            _chatCode = null;
            PreviewText = _share.Text;
            SuggestedFileName = _share.FileName;
            Card = CapabilityCardViewModel.ForPack(_share.Pack, Loc.Prompt_PortableFooter);
            PrivacyNotes.Clear();
            foreach (var note in DescribePrivacy(_share.Pack, _share.Findings))
                PrivacyNotes.Add(note);
            SetStatus(Loc.Prompt_ReviewReminder);
        }
        catch (ArgumentException ex)
        {
            _share = null;
            PreviewText = "";
            SetStatus(string.Format(CultureInfo.CurrentCulture, Loc.Prompt_InvalidDraft, ex.Message), isError: true);
        }
        IsCopied = false;
        IsChatCopied = false;
        _copyGeneration++;
        IsChatCodeLong = false;
        OnPropertyChanged(nameof(ShareText));
        OnPropertyChanged(nameof(PromptInputs));
        OnPropertyChanged(nameof(HasPromptInputs));
        NotifyShareAvailability();
    }

    private void NotifyShareAvailability()
    {
        OnPropertyChanged(nameof(CanShare));
        CopyCommand.NotifyCanExecuteChanged();
        CopyForChatCommand.NotifyCanExecuteChanged();
        CopyConfigCommand.NotifyCanExecuteChanged();
    }
}
