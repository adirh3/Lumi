using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;
using Lumi.Services.Sharing;

namespace Lumi.ViewModels;

public enum ImportSheetStage
{
    Pick,
    Review,
    Done
}

/// <summary>One thing the import will add (or reuse), with the facts that matter for trusting it.</summary>
public partial class ImportItemViewModel : ObservableObject
{
    public ImportItemViewModel(
        SharedCapabilityKind kind,
        string glyph,
        string name,
        ImportItemStatus status,
        string statusLabel,
        string? description,
        string? preview,
        IEnumerable<ReceiptNoteViewModel> facts)
    {
        Kind = kind;
        Glyph = glyph;
        Name = name;
        Status = status;
        StatusLabel = statusLabel;
        Description = description ?? "";
        Preview = preview ?? "";
        Facts = [.. facts];
    }

    public SharedCapabilityKind Kind { get; }
    public string Glyph { get; }
    public string Name { get; }
    public string KindLabel => CapabilityCardViewModel.KindName(Kind);
    public ImportItemStatus Status { get; }
    public string StatusLabel { get; }
    public string Description { get; }
    public string Preview { get; }
    public ObservableCollection<ReceiptNoteViewModel> Facts { get; }

    public bool HasDescription => Description.Length > 0;
    public bool HasPreview => Preview.Length > 0;
    public bool IsReused => Status == ImportItemStatus.Reused;
    public bool IsRenamed => Status == ImportItemStatus.Renamed;
    public bool IsNew => Status == ImportItemStatus.New;
    public IBrush Accent => CapabilityCardViewModel.AccentFor(Kind);
    public IBrush AccentTint => CapabilityCardViewModel.TintFor(Kind);

    public string PreviewToggleLabel => IsExpanded ? Loc.Import_HideInstructions : Loc.Import_ShowInstructions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewToggleLabel))]
    private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

public enum NextStepAction
{
    SetUpServer,
    ChatWithLumi,
    OpenItem
}

public sealed class NextStepViewModel(
    NextStepAction action,
    string glyph,
    string title,
    string detail,
    string actionLabel,
    SharedCapabilityKind itemKind,
    Guid itemId)
{
    public NextStepAction Action { get; } = action;
    public string Glyph { get; } = glyph;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public string ActionLabel { get; } = actionLabel;
    public SharedCapabilityKind ItemKind { get; } = itemKind;
    public Guid ItemId { get; } = itemId;
}

/// <summary>
/// The import sheet: pick (drop, paste, browse — or accept what is already on the clipboard), review a
/// receipt of exactly what would be added and what it can do, then add it. Nothing is written until
/// <see cref="ConfirmCommand"/>; the receipt is planned by <see cref="CapabilityImporter"/> against the
/// live library and planned again at the moment of adding.
/// </summary>
public partial class ImportSheetViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private CapabilityPack? _pack;
    private string? _clipboardText;
    private int _openGeneration;

    public ImportSheetViewModel(DataStore dataStore)
    {
        _dataStore = dataStore;
        Notes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNotes));
    }

    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPicking), nameof(IsReviewing), nameof(IsDone))]
    private ImportSheetStage _stage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClipboardCandidate))]
    private string? _clipboardCandidateName;

    [ObservableProperty] private string _clipboardCandidateDetail = "";
    [ObservableProperty] private string _clipboardCandidateGlyph = "";
    [ObservableProperty] private CapabilityCardViewModel? _card;
    [ObservableProperty] private string _riskTitle = "";
    [ObservableProperty] private string _riskText = "";
    [ObservableProperty] private string _riskGlyph = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRiskSafe), nameof(IsRiskInfo), nameof(IsRiskCaution))]
    private ImportRisk _risk;

    [ObservableProperty] private string _reviewSummary = "";
    [ObservableProperty] private string _confirmLabel = "";
    [ObservableProperty] private bool _canConfirm;
    [ObservableProperty] private string _doneTitle = "";
    [ObservableProperty] private string _doneSubtitle = "";
    [ObservableProperty] private bool _isDropTargetActive;

    public ObservableCollection<ImportItemViewModel> Items { get; } = [];
    public ObservableCollection<ReceiptNoteViewModel> Notes { get; } = [];
    public ObservableCollection<NextStepViewModel> NextSteps { get; } = [];

    public bool IsPicking => Stage == ImportSheetStage.Pick;
    public bool IsReviewing => Stage == ImportSheetStage.Review;
    public bool IsDone => Stage == ImportSheetStage.Done;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasClipboardCandidate => ClipboardCandidateName is not null;
    public bool HasNotes => Notes.Count > 0;
    public bool IsRiskSafe => Risk == ImportRisk.InstructionsOnly;
    public bool IsRiskInfo => Risk == ImportRisk.ConnectsToInternet;
    public bool IsRiskCaution => Risk == ImportRisk.RunsOnThisComputer;

    /// <summary>Raised after items were added and saved, so every chat surface can pick them up.</summary>
    public event Action<ImportOutcome>? Imported;

    /// <summary>Raised when the user wants to see an imported skill, Lumi or MCP server in its page.</summary>
    public event Action<SharedCapabilityKind, Guid>? OpenItemRequested;

    /// <summary>Raised when the user wants to start chatting with an imported Lumi.</summary>
    public event Action<Guid>? ChatWithLumiRequested;

    public void Open()
    {
        Reset();
        IsOpen = true;
        _ = ProbeClipboardAsync(_openGeneration);
    }

    /// <summary>Opens straight to the receipt for a dropped or opened file.</summary>
    public void OpenWithFile(string path)
    {
        Reset();
        IsOpen = true;
        LoadFile(path);
    }

    public void OpenWithText(string text, string sourceLabel)
    {
        Reset();
        IsOpen = true;
        LoadText(text, sourceLabel);
    }

    public async Task OpenWithStorageItemAsync(IStorageItem item)
    {
        Reset();
        IsOpen = true;
        await LoadStorageItemAsync(item);
    }

    /// <summary>Accepts a dropped or picked file, or a skill folder (its SKILL.md is read).</summary>
    public async Task LoadStorageItemAsync(IStorageItem item)
    {
        if (item is IStorageFile file)
        {
            await LoadStorageFileAsync(file);
            return;
        }

        if (item.TryGetLocalPath() is { } folder && Directory.Exists(folder))
        {
            var skillFile = Path.Combine(folder, CapabilityPackWriter.SkillFileName);
            if (File.Exists(skillFile))
                LoadFile(skillFile);
            else
                ShowError(Loc.Import_ErrorNotRecognized);
        }
    }

    private void Reset()
    {
        _openGeneration++;
        _pack = null;
        _clipboardText = null;
        ClipboardCandidateName = null;
        ErrorMessage = null;
        IsDropTargetActive = false;
        Items.Clear();
        Notes.Clear();
        NextSteps.Clear();
        Card = null;
        Stage = ImportSheetStage.Pick;
    }

    /// <summary>
    /// Offers whatever capability is already on the clipboard, so "copy on one machine, open Import on
    /// the other" needs no further clicks. The clipboard is only read locally and only parsed.
    /// </summary>
    private async Task ProbeClipboardAsync(int generation)
    {
        var text = await ClipboardHelper.GetTextAsync();
        if (generation != _openGeneration || !IsOpen || Stage != ImportSheetStage.Pick
            || string.IsNullOrWhiteSpace(text) || text.Length > CapabilityPackReader.MaxTextLength)
            return;

        var result = CapabilityPackReader.Read(text);
        if (!result.Success)
            return;

        _clipboardText = text;
        var pack = result.Pack!;
        ClipboardCandidateGlyph = CapabilityCardViewModel.ForPack(pack, "").Glyph;
        ClipboardCandidateDetail = CapabilityCardViewModel.FormatName(pack.Format) + " · "
                                   + CapabilityCardViewModel.DescribeContents(pack);
        ClipboardCandidateName = pack.Name;
    }

    [RelayCommand]
    private void UseClipboardCandidate()
    {
        if (_clipboardText is not null)
            LoadText(_clipboardText, Loc.Import_FromClipboard);
    }

    [RelayCommand]
    private async Task Paste()
    {
        var text = await ClipboardHelper.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            ErrorMessage = Loc.Import_ErrorClipboardEmpty;
            return;
        }

        LoadText(text, Loc.Import_FromClipboard);
    }

    public void LoadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                ShowError(string.Format(CultureInfo.CurrentCulture, Loc.Import_ErrorRead, Path.GetFileName(path)));
                return;
            }

            // A UTF-8 character is at least one byte, so anything this large cannot be under the text limit.
            if (info.Length > CapabilityPackReader.MaxTextLength * 4L)
            {
                ShowError(Loc.Import_ErrorTooLarge);
                return;
            }

            var label = string.Equals(info.Name, CapabilityPackWriter.SkillFileName, StringComparison.OrdinalIgnoreCase)
                        && info.Directory is { } directory
                ? directory.Name + "/" + info.Name
                : info.Name;
            LoadText(File.ReadAllText(path), label, info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowError(string.Format(CultureInfo.CurrentCulture, Loc.Import_ErrorRead, ex.Message));
        }
    }

    public async Task LoadStorageFileAsync(IStorageFile file)
    {
        if (file.TryGetLocalPath() is { } localPath)
        {
            LoadFile(localPath);
            return;
        }

        try
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var buffer = new char[CapabilityPackReader.MaxTextLength + 1];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            if (read > CapabilityPackReader.MaxTextLength)
            {
                ShowError(Loc.Import_ErrorTooLarge);
                return;
            }

            LoadText(new string(buffer, 0, read), file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(string.Format(CultureInfo.CurrentCulture, Loc.Import_ErrorRead, ex.Message));
        }
    }

    public bool LoadText(string text, string sourceLabel, string? sourcePath = null)
    {
        var result = CapabilityPackReader.Read(text, sourcePath);
        if (!result.Success)
        {
            ShowError(DescribeError(result));
            return false;
        }

        Review(result.Pack!, sourceLabel + " · " + PackText.FormatSize(text));
        return true;
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
        Stage = ImportSheetStage.Pick;
    }

    private void Review(CapabilityPack pack, string footer)
    {
        _pack = pack;
        var plan = CapabilityImporter.Plan(pack, _dataStore);

        Card = CapabilityCardViewModel.ForPack(pack, footer);
        ErrorMessage = null;

        Items.Clear();
        if (plan.Lumi is { } lumi)
            Items.Add(DescribeLumi(lumi, plan));
        foreach (var skill in plan.Skills)
            Items.Add(DescribeSkill(skill));
        foreach (var server in plan.McpServers)
            Items.Add(DescribeServer(server));

        Notes.Clear();
        if (plan.HiddenCharactersRemoved > 0)
        {
            Notes.Add(new ReceiptNoteViewModel(
                "🧹",
                string.Format(CultureInfo.CurrentCulture, Loc.Import_NoteHidden, plan.HiddenCharactersRemoved),
                ReceiptTone.Caution));
        }

        foreach (var note in pack.Notes)
        {
            Notes.Add(new ReceiptNoteViewModel(
                "ℹ",
                string.Format(
                    CultureInfo.CurrentCulture,
                    note.Kind == PackNoteKind.MissingReference ? Loc.Import_NoteMissing : Loc.Import_NoteSkipped,
                    note.Detail),
                ReceiptTone.Info));
        }

        Risk = plan.Risk;
        (RiskGlyph, RiskTitle, RiskText) = plan.Risk switch
        {
            ImportRisk.RunsOnThisComputer => ("⚠", Loc.Import_RiskLocalTitle, Loc.Import_RiskLocalText),
            ImportRisk.ConnectsToInternet => ("🌐", Loc.Import_RiskNetworkTitle, Loc.Import_RiskNetworkText),
            // Nothing new runs — but a Lumi can still use servers that are already set up here.
            _ when plan.McpServers.Count > 0 => ("🛡", Loc.Import_RiskSafeTitle, Loc.Import_RiskSafeReusedText),
            _ => ("🛡", Loc.Import_RiskSafeTitle, Loc.Import_RiskSafeText)
        };

        ReviewSummary = plan.ReusedItemCount > 0
            ? string.Format(CultureInfo.CurrentCulture, Loc.Import_SummaryWithReused, plan.NewItemCount, plan.ReusedItemCount)
            : string.Format(CultureInfo.CurrentCulture, Loc.Import_Summary, plan.NewItemCount);
        CanConfirm = plan.NewItemCount > 0;
        ConfirmLabel = CanConfirm ? Loc.Import_Confirm : Loc.Import_NothingNew;
        Stage = ImportSheetStage.Review;
    }

    private static ImportItemViewModel DescribeLumi(PlannedLumi lumi, ImportPlan plan)
    {
        var facts = new List<ReceiptNoteViewModel>();
        var tools = lumi.Source.ToolNames;
        facts.Add(tools is null
            ? new ReceiptNoteViewModel("🧰", Loc.Import_FactToolsAll, ReceiptTone.Info)
            : tools.Count == 0
                ? new ReceiptNoteViewModel("🧰", Loc.Import_FactToolsNone, ReceiptTone.Safe)
                : new ReceiptNoteViewModel(
                    "🧰",
                    string.Format(CultureInfo.CurrentCulture, Loc.Import_FactToolsSome, ShareSheetViewModel.DescribeTools(tools))));

        var uses = new List<string>();
        if (plan.Skills.Count > 0)
            uses.Add(CapabilityCardViewModel.Count(plan.Skills.Count, Loc.Capability_OneSkill, Loc.Capability_ManySkills));
        if (plan.McpServers.Count > 0)
            uses.Add(CapabilityCardViewModel.Count(plan.McpServers.Count, Loc.Capability_OneServer, Loc.Capability_ManyServers));
        if (uses.Count > 0)
            facts.Add(new ReceiptNoteViewModel("🧩", string.Format(CultureInfo.CurrentCulture, Loc.Import_FactUses, string.Join(" · ", uses))));

        return new ImportItemViewModel(
            SharedCapabilityKind.Lumi,
            lumi.Source.IconGlyph,
            lumi.Name,
            lumi.Status,
            StatusLabel(lumi.Status),
            lumi.Description,
            Truncate(lumi.SystemPrompt),
            facts);
    }

    private static ImportItemViewModel DescribeSkill(PlannedSkill skill)
    {
        var facts = new List<ReceiptNoteViewModel>();
        if (skill.Status == ImportItemStatus.Reused)
            facts.Add(new ReceiptNoteViewModel("✓", Loc.Import_FactReusedSkill, ReceiptTone.Safe));
        if (skill.Source.License is { } license)
            facts.Add(new ReceiptNoteViewModel("📄", string.Format(CultureInfo.CurrentCulture, Loc.Import_FactLicense, license)));
        if (skill.Source.CompanionFiles.Count > 0)
        {
            var names = skill.Source.CompanionFiles;
            facts.Add(new ReceiptNoteViewModel(
                "📎",
                string.Format(CultureInfo.CurrentCulture, Loc.Import_FactCompanions, names.Count),
                ReceiptTone.Info,
                code: string.Join(", ", names.Take(6)) + (names.Count > 6 ? ", …" : "")));
        }

        return new ImportItemViewModel(
            SharedCapabilityKind.Skill,
            skill.Source.IconGlyph,
            skill.Name,
            skill.Status,
            StatusLabel(skill.Status),
            skill.Description,
            Truncate(skill.Content),
            facts);
    }

    private static ImportItemViewModel DescribeServer(PlannedMcpServer server)
    {
        var facts = new List<ReceiptNoteViewModel>();
        var source = server.Source;
        if (server.Status == ImportItemStatus.Reused)
        {
            facts.Add(new ReceiptNoteViewModel(
                "✓",
                string.Format(CultureInfo.CurrentCulture, Loc.Import_FactReusedServer, server.Name),
                ReceiptTone.Safe));
        }
        else
        {
            if (source.IsRemote)
            {
                facts.Add(new ReceiptNoteViewModel(
                    "🌐",
                    string.Format(CultureInfo.CurrentCulture, Loc.Import_FactConnects, HostOf(server.Url)),
                    ReceiptTone.Info,
                    code: server.Url));
            }
            else
            {
                var commandLine = new SharedMcpServer("", "", false, server.Command, server.Args, "", [], [], [], null).CommandLine;
                facts.Add(new ReceiptNoteViewModel("▶", Loc.Import_FactRunsLocal, ReceiptTone.Caution, code: commandLine));
            }

            if (source.RequiredKeys.Count > 0)
            {
                facts.Add(new ReceiptNoteViewModel(
                    "🔑",
                    Loc.Import_FactNeedsKeys,
                    ReceiptTone.Info,
                    code: HiddenText.Strip(string.Join(", ", source.RequiredKeys), out _),
                    detail: Loc.Import_FactKeysNote));
            }

            if (source.HasRedactedValues)
                facts.Add(new ReceiptNoteViewModel("✂", Loc.Import_FactRedacted, ReceiptTone.Info));

            var paths = server.Args.Where(SecretRedactor.LooksLikeLocalPath).ToList();
            if (paths.Count > 0)
                facts.Add(new ReceiptNoteViewModel("📁", Loc.Import_FactLocalPaths, ReceiptTone.Info, code: string.Join("\n", paths)));

            facts.Add(new ReceiptNoteViewModel("⏻", Loc.Import_FactAddedOff, ReceiptTone.Safe));
        }

        return new ImportItemViewModel(
            SharedCapabilityKind.McpServer,
            "🔌",
            server.Name,
            server.Status,
            StatusLabel(server.Status),
            server.Description,
            null,
            facts);
    }

    private static string StatusLabel(ImportItemStatus status) => status switch
    {
        ImportItemStatus.Reused => Loc.Import_StatusReused,
        ImportItemStatus.Renamed => Loc.Import_StatusRenamed,
        _ => Loc.Import_StatusNew
    };

    private static string HostOf(string url)
        => Uri.TryCreate(url.Replace(SecretRedactor.Placeholder, "x", StringComparison.Ordinal), UriKind.Absolute, out var uri)
            ? uri.Host
            : url;

    /// <summary>
    /// The receipt shows instructions in full. Only text far beyond any real skill is cut, and then the
    /// cut is stated, so nothing is imported silently.
    /// </summary>
    private static string Truncate(string text)
    {
        const int limit = 60_000;
        return text.Length <= limit
            ? text
            : text[..limit].TrimEnd() + "\n\n" + string.Format(
                CultureInfo.CurrentCulture,
                Loc.Import_PreviewTruncated,
                limit.ToString("N0", CultureInfo.CurrentCulture),
                (text.Length - limit).ToString("N0", CultureInfo.CurrentCulture));
    }

    private static string DescribeError(PackReadResult result) => result.Error switch
    {
        PackReadError.Empty => Loc.Import_ErrorEmpty,
        PackReadError.TooLarge => Loc.Import_ErrorTooLarge,
        PackReadError.MalformedFrontmatter => string.Format(CultureInfo.CurrentCulture, Loc.Import_ErrorFrontmatter, result.Detail ?? ""),
        PackReadError.SkillWithoutName => Loc.Import_ErrorSkillName,
        PackReadError.SkillWithoutInstructions => Loc.Import_ErrorSkillBody,
        PackReadError.InvalidJson => string.Format(CultureInfo.CurrentCulture, Loc.Import_ErrorJson, result.Detail ?? ""),
        PackReadError.NoMcpServers => Loc.Import_ErrorNoServers,
        PackReadError.NewerVersion => Loc.Import_ErrorNewer,
        PackReadError.EmptyPack => Loc.Import_ErrorEmptyPack,
        PackReadError.DamagedCode => Loc.Import_ErrorDamagedCode,
        _ => Loc.Import_ErrorNotRecognized
    };

    [RelayCommand]
    private void Confirm()
    {
        if (_pack is null || Stage != ImportSheetStage.Review)
            return;

        // Plan again at the moment of adding: the library may have changed since the receipt was shown.
        var plan = CapabilityImporter.Plan(_pack, _dataStore);
        if (plan.NewItemCount == 0)
            return;

        var outcome = CapabilityImporter.Apply(plan, _dataStore);
        _ = _dataStore.SaveAsync();
        if (outcome.AddedSkills.Count > 0)
            _dataStore.SyncSkillFiles();

        Imported?.Invoke(outcome);
        Finish(outcome, plan);
    }

    private void Finish(ImportOutcome outcome, ImportPlan plan)
    {
        NextSteps.Clear();
        foreach (var server in outcome.AddedMcpServers)
        {
            var keys = (server.ServerType == "remote" ? server.Headers.Keys : server.Env.Keys).ToList();
            NextSteps.Add(new NextStepViewModel(
                NextStepAction.SetUpServer,
                "🔌",
                string.Format(CultureInfo.CurrentCulture, Loc.Import_StepSetUp, server.Name),
                keys.Count > 0
                    ? string.Format(CultureInfo.CurrentCulture, Loc.Import_StepSetUpKeys, string.Join(", ", keys))
                    : Loc.Import_StepSetUpReview,
                Loc.Import_SetUp,
                SharedCapabilityKind.McpServer,
                server.Id));
        }

        if (outcome.AddedLumi is { } agent)
        {
            NextSteps.Add(new NextStepViewModel(
                NextStepAction.ChatWithLumi,
                agent.IconGlyph,
                string.Format(CultureInfo.CurrentCulture, Loc.Import_StepChat, agent.Name),
                Loc.Import_StepChatDetail,
                Loc.Import_StartChat,
                SharedCapabilityKind.Lumi,
                agent.Id));
        }
        else if (outcome.PrimaryId is { } primaryId && outcome.PrimaryKind == SharedCapabilityKind.Skill)
        {
            NextSteps.Add(new NextStepViewModel(
                NextStepAction.OpenItem,
                plan.Skills.FirstOrDefault()?.Source.IconGlyph ?? "⚡",
                string.Format(CultureInfo.CurrentCulture, Loc.Import_StepOpen, outcome.PrimaryName),
                Loc.Import_StepOpenDetail,
                Loc.Import_Open,
                SharedCapabilityKind.Skill,
                primaryId));
        }

        var added = outcome.AddedSkills.Count + outcome.AddedMcpServers.Count + (outcome.AddedLumi is null ? 0 : 1);
        DoneTitle = Loc.Import_DoneTitle;
        DoneSubtitle = added == 1 && outcome.ReusedCount == 0
            ? string.Format(CultureInfo.CurrentCulture, Loc.Import_DoneOne, outcome.PrimaryName)
            : outcome.ReusedCount > 0
                ? string.Format(CultureInfo.CurrentCulture, Loc.Import_DoneManyReused, added, outcome.ReusedCount)
                : string.Format(CultureInfo.CurrentCulture, Loc.Import_DoneMany, added);
        Stage = ImportSheetStage.Done;
    }

    [RelayCommand]
    private void RunStep(NextStepViewModel? step)
    {
        if (step is null)
            return;

        IsOpen = false;
        if (step.Action == NextStepAction.ChatWithLumi)
            ChatWithLumiRequested?.Invoke(step.ItemId);
        else
            OpenItemRequested?.Invoke(step.ItemKind, step.ItemId);
    }

    [RelayCommand]
    private void Back()
    {
        ErrorMessage = null;
        Stage = ImportSheetStage.Pick;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;
}
