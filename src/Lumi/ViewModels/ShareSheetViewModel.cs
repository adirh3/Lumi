using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Sharing;

namespace Lumi.ViewModels;

/// <summary>An agent tool the skill can be sent to, and whether it already has this exact skill.</summary>
public partial class SkillTargetViewModel : ObservableObject
{
    public SkillTargetViewModel(SkillToolTarget target, SkillInstallPlan plan)
    {
        Target = target;
        _plan = plan;
    }

    public SkillToolTarget Target { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(IsInstalled), nameof(NeedsUpdate), nameof(CanSend), nameof(ToolTip))]
    private SkillInstallPlan _plan;

    public SkillTargetState State => Plan.State;
    public string DisplayName => Target.DisplayName;
    public bool IsInstalled => State == SkillTargetState.UpToDate;
    public bool NeedsUpdate => State == SkillTargetState.Different;
    public bool CanSend => State != SkillTargetState.UpToDate;

    public string ToolTip => State switch
    {
        SkillTargetState.UpToDate => string.Format(CultureInfo.CurrentCulture, Loc.Share_TargetInstalledTip, DisplayName),
        SkillTargetState.Different => string.Format(CultureInfo.CurrentCulture, Loc.Share_TargetUpdateTip, DisplayName),
        _ => string.Format(CultureInfo.CurrentCulture, Loc.Share_TargetSendTip, Plan.Slug + "/" + CapabilityPackWriter.SkillFileName, Target.SkillsDirectory)
    };
}

/// <summary>
/// The share sheet: a card for the capability, a receipt of exactly what leaves the computer (and
/// what was kept back), and every way to send it — clipboard, file, a skills folder, or straight into
/// another agent tool. The text is produced by <see cref="CapabilityPackWriter"/>; this class only
/// presents it and performs the chosen hand-off.
/// </summary>
public partial class ShareSheetViewModel : ObservableObject
{
    private readonly DataStore _dataStore;
    private readonly Func<IReadOnlyList<SkillToolTarget>> _discoverTargets;
    private CapabilityShare? _share;
    private int _copyGeneration;

    public ShareSheetViewModel(DataStore dataStore, Func<IReadOnlyList<SkillToolTarget>>? discoverTargets = null)
    {
        _dataStore = dataStore;
        _discoverTargets = discoverTargets ?? (static () => SkillToolTargets.Discover());
        Targets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTargets));
        PrivacyNotes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPrivacyNotes));
        Contents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasManyContents));
    }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private CapabilityCardViewModel? _card;
    [ObservableProperty] private bool _isCopied;
    [ObservableProperty] private bool _isConfigCopied;
    [ObservableProperty] private bool _isSkill;
    [ObservableProperty] private bool _isMcpServer;
    [ObservableProperty] private string _interopText = "";
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private bool _isPreviewExpanded;
    [ObservableProperty] private string _suggestedFileName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    [ObservableProperty] private bool _isStatusError;

    /// <summary>The editor next to the share button holds changes that are not in the shared (saved) version.</summary>
    [ObservableProperty] private bool _hasUnsavedEdits;

    public ObservableCollection<ReceiptNoteViewModel> Contents { get; } = [];
    public ObservableCollection<ReceiptNoteViewModel> PrivacyNotes { get; } = [];
    public ObservableCollection<SkillTargetViewModel> Targets { get; } = [];

    public bool HasTargets => Targets.Count > 0;
    public bool HasPrivacyNotes => PrivacyNotes.Count > 0;

    /// <summary>
    /// A single skill is already fully described by the card; bundles list their parts, and a server
    /// always shows the exact command or URL it carries.
    /// </summary>
    public bool HasManyContents => Contents.Count > 1 || Contents.Any(static line => line.HasCode);

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>The exact text that is copied or saved.</summary>
    public string ShareText => _share?.Text ?? "";

    public CapabilityShare? Share => _share;

    private SharedSkill? SharedSkill => _share?.Pack.Format == CapabilityPackFormat.SkillMarkdown ? _share.Pack.Skills[0] : null;

    public void OpenFor(Skill skill, bool hasUnsavedEdits = false)
        => Load(CapabilityPackWriter.ForSkill(skill), Loc.Share_TitleSkill, hasUnsavedEdits);

    public void OpenFor(LumiAgent agent, bool hasUnsavedEdits = false)
        => Load(CapabilityPackWriter.ForLumi(agent, _dataStore.Data), Loc.Share_TitleLumi, hasUnsavedEdits);

    public void OpenFor(McpServer server, bool hasUnsavedEdits = false)
        => Load(CapabilityPackWriter.ForMcpServer(server), Loc.Share_TitleMcp, hasUnsavedEdits);

    private void Load(CapabilityShare share, string title, bool hasUnsavedEdits)
    {
        _share = share;
        _copyGeneration++;
        var pack = share.Pack;
        HasUnsavedEdits = hasUnsavedEdits;

        Title = title;
        IsSkill = pack.Format == CapabilityPackFormat.SkillMarkdown;
        IsMcpServer = pack.Kind == SharedCapabilityKind.McpServer;
        IsCopied = false;
        IsConfigCopied = false;
        IsPreviewExpanded = false;
        StatusMessage = null;
        IsStatusError = false;
        SuggestedFileName = IsSkill ? CapabilityPackWriter.SkillFileName : share.FileName;
        PreviewText = share.Text;
        InteropText = IsSkill ? Loc.Share_InteropSkill : Loc.Share_InteropPack;
        Card = CapabilityCardViewModel.ForPack(
            pack,
            string.Format(CultureInfo.CurrentCulture, Loc.Share_Footer, PackText.FormatSize(share.Text)));

        Contents.Clear();
        foreach (var line in DescribeContents(pack))
            Contents.Add(line);

        PrivacyNotes.Clear();
        foreach (var note in DescribePrivacy(pack, share.Findings))
            PrivacyNotes.Add(note);

        Targets.Clear();
        if (SharedSkill is { } skill)
        {
            foreach (var target in _discoverTargets())
            {
                try
                {
                    Targets.Add(new SkillTargetViewModel(target, SkillToolTargets.PlanInstall(target.SkillsDirectory, skill)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A tool folder we cannot plan into is simply not offered.
                }
            }
        }

        OnPropertyChanged(nameof(ShareText));
        OnPropertyChanged(nameof(Share));
        IsOpen = true;
    }

    private static IEnumerable<ReceiptNoteViewModel> DescribeContents(CapabilityPack pack)
    {
        if (pack.Lumi is { } lumi)
        {
            yield return new ReceiptNoteViewModel(
                lumi.IconGlyph,
                lumi.Name,
                detail: Loc.Capability_KindLumi + " · " + DescribeTools(lumi.ToolNames));
        }

        foreach (var skill in pack.Skills)
        {
            yield return new ReceiptNoteViewModel(
                skill.IconGlyph,
                skill.Name,
                detail: Loc.Capability_KindSkill + " · " + PackText.FormatSize(skill.Content));
        }

        foreach (var server in pack.McpServers)
        {
            yield return new ReceiptNoteViewModel(
                "🔌",
                server.Name,
                code: server.CommandLine,
                detail: server.IsRemote ? Loc.Share_ServerConnects : Loc.Share_ServerRuns);
        }
    }

    internal static string DescribeTools(IReadOnlyList<string>? toolNames)
        => toolNames is null
            ? Loc.Capability_AllTools
            : toolNames.Count == 0
                ? Loc.Capability_NoTools
                : string.Join(", ", toolNames.Select(AgentsViewModel.GetToolDisplayName));

    private static IEnumerable<ReceiptNoteViewModel> DescribePrivacy(CapabilityPack pack, IReadOnlyList<ShareFinding> findings)
    {
        foreach (var group in findings
                     .Where(static finding => finding.Kind == ShareFindingKind.SecretValueRemoved)
                     .GroupBy(static finding => finding.ItemName))
        {
            yield return new ReceiptNoteViewModel(
                "🔒",
                string.Format(CultureInfo.CurrentCulture, Loc.ShareNote_SecretsRemoved, group.Key),
                ReceiptTone.Safe,
                code: string.Join(", ", group.Select(static finding => finding.Detail)));
        }

        foreach (var finding in findings.Where(static finding =>
                     finding.Kind is ShareFindingKind.CredentialRemovedFromArgument or ShareFindingKind.CredentialRemovedFromUrl))
        {
            yield return new ReceiptNoteViewModel(
                "✂",
                string.Format(
                    CultureInfo.CurrentCulture,
                    finding.Kind == ShareFindingKind.CredentialRemovedFromUrl ? Loc.ShareNote_UrlCredential : Loc.ShareNote_ArgumentCredential,
                    finding.ItemName),
                ReceiptTone.Safe,
                code: finding.Detail);
        }

        foreach (var group in findings
                     .Where(static finding => finding.Kind == ShareFindingKind.LocalPath)
                     .GroupBy(static finding => finding.ItemName))
        {
            yield return new ReceiptNoteViewModel(
                "📁",
                string.Format(CultureInfo.CurrentCulture, Loc.ShareNote_LocalPath, group.Key),
                ReceiptTone.Info,
                code: string.Join("\n", group.Select(static finding => finding.Detail)));
        }

        foreach (var group in findings
                     .Where(static finding => finding.Kind == ShareFindingKind.TextLooksLikeSecret)
                     .GroupBy(static finding => finding.ItemName))
        {
            yield return new ReceiptNoteViewModel(
                "⚠",
                string.Format(CultureInfo.CurrentCulture, Loc.ShareNote_TextSecret, group.Key),
                ReceiptTone.Caution,
                code: string.Join(", ", group.Select(static finding => finding.Detail)));
        }

        yield return pack.McpServers.Count > 0
            ? new ReceiptNoteViewModel("🛡", Loc.ShareNote_NeverShared, ReceiptTone.Safe)
            : new ReceiptNoteViewModel("🛡", Loc.ShareNote_InstructionsOnly, ReceiptTone.Safe);
    }

    [RelayCommand]
    private async Task Copy()
    {
        if (_share is null)
            return;

        await ClipboardHelper.CopyTextAsync(_share.Text);
        IsConfigCopied = false;
        IsCopied = true;
        await ResetCopiedAsync(++_copyGeneration);
    }

    [RelayCommand]
    private async Task CopyConfig()
    {
        if (_share is null)
            return;

        await ClipboardHelper.CopyTextAsync(CapabilityPackWriter.WriteMcpConfig(_share.Pack.McpServers));
        IsCopied = false;
        IsConfigCopied = true;
        await ResetCopiedAsync(++_copyGeneration);
    }

    private async Task ResetCopiedAsync(int generation)
    {
        await Task.Delay(1800);
        if (generation != _copyGeneration)
            return;

        IsCopied = false;
        IsConfigCopied = false;
    }

    [RelayCommand]
    private void SendToTarget(SkillTargetViewModel? target)
    {
        if (target is null || SharedSkill is not { } skill)
            return;

        try
        {
            var wasDifferent = target.State == SkillTargetState.Different;
            target.Plan = SkillToolTargets.Install(target.Target.SkillsDirectory, skill);
            SetStatus(string.Format(
                CultureInfo.CurrentCulture,
                wasDifferent ? Loc.Share_UpdatedIn : Loc.Share_SentTo,
                target.DisplayName,
                target.Plan.FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus(string.Format(CultureInfo.CurrentCulture, Loc.Share_SaveFailed, ex.Message), isError: true);
        }
    }

    /// <summary>Writes the canonical Agent Skills layout (<c>&lt;folder&gt;/&lt;name&gt;/SKILL.md</c>) into a chosen folder.</summary>
    public void SaveSkillToFolder(string folder)
    {
        if (SharedSkill is not { } skill || string.IsNullOrWhiteSpace(folder))
            return;

        try
        {
            // Picking the skill's own folder should not nest it a second time.
            var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = string.Equals(Path.GetFileName(trimmed), skill.Slug, StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(trimmed) ?? trimmed
                : trimmed;
            var plan = SkillToolTargets.Install(root, skill);
            SetStatus(string.Format(CultureInfo.CurrentCulture, Loc.Share_SavedTo, plan.FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus(string.Format(CultureInfo.CurrentCulture, Loc.Share_SaveFailed, ex.Message), isError: true);
        }
    }

    public async Task SaveToFileAsync(IStorageFile file)
    {
        if (_share is null)
            return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                await writer.WriteAsync(_share.Text);
            SetStatus(string.Format(CultureInfo.CurrentCulture, Loc.Share_SavedTo, file.TryGetLocalPath() ?? file.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(string.Format(CultureInfo.CurrentCulture, Loc.Share_SaveFailed, ex.Message), isError: true);
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        IsStatusError = isError;
        StatusMessage = message;
    }

    [RelayCommand]
    private void TogglePreview() => IsPreviewExpanded = !IsPreviewExpanded;

    [RelayCommand]
    private void Close() => IsOpen = false;
}
