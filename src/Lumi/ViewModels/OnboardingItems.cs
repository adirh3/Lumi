using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services.Onboarding;

namespace Lumi.ViewModels;

/// <summary>One dot of the onboarding progress indicator.</summary>
public sealed partial class OnboardingStepDot : ObservableObject
{
    public OnboardingStepDot(int index) => Index = index;

    public int Index { get; }

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isDone;
}

/// <summary>Something Lumi can do, told as a tiny conversation: a request, the steps Lumi took, and its reply.</summary>
public sealed partial class OnboardingCapability : ObservableObject
{
    private readonly Func<Geometry> _icon;

    public OnboardingCapability(string key, Func<Geometry> icon, string chip, string title, string description,
        string request, IReadOnlyList<string> steps, string reply)
    {
        Key = key;
        _icon = icon;
        Chip = chip;
        Title = title;
        Description = description;
        Request = request;
        Steps = steps;
        Reply = reply;
    }

    public string Key { get; }

    /// <summary>Parsed when the view asks for it, so the view model never needs a render platform.</summary>
    public Geometry Icon => _icon();

    public string Chip { get; }
    public string Title { get; }
    public string Description { get; }
    public string Request { get; }
    public IReadOnlyList<string> Steps { get; }
    public string Reply { get; }

    [ObservableProperty] private bool _isSelected;

    internal static OnboardingCapability FromStrings(string key, Func<Geometry> icon, string prefix)
        => new(
            key,
            icon,
            Loc.Get($"Onboarding_Cap{prefix}Chip"),
            Loc.Get($"Onboarding_Cap{prefix}Title"),
            Loc.Get($"Onboarding_Cap{prefix}Description"),
            Loc.Get($"Onboarding_Cap{prefix}Request"),
            Loc.Get($"Onboarding_Cap{prefix}Steps").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Loc.Get($"Onboarding_Cap{prefix}Reply"));
}

public enum OnboardingSourceState { Waiting, Scanning, Done, Skipped }

/// <summary>A kind of local signal the user can allow onboarding to read, with its scan progress.</summary>
public sealed partial class OnboardingSource : ObservableObject
{
    private readonly Func<Geometry> _icon;

    public OnboardingSource(string key, Func<Geometry> icon, string title, string description)
    {
        Key = key;
        _icon = icon;
        Title = title;
        Description = description;
    }

    public string Key { get; }
    public Geometry Icon => _icon();
    public string Title { get; }
    public string Description { get; }

    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsScanning), nameof(IsDone), nameof(IsSkipped))]
    private OnboardingSourceState _state;

    public bool IsWaiting => State == OnboardingSourceState.Waiting;
    public bool IsScanning => State == OnboardingSourceState.Scanning;
    public bool IsDone => State == OnboardingSourceState.Done;
    public bool IsSkipped => State == OnboardingSourceState.Skipped;
}

/// <summary>A discovered folder the user can turn into a Lumi project.</summary>
public sealed partial class OnboardingProjectCandidate : ObservableObject
{
    // Mid-tone hues that read on dark and light surfaces alike (the launchpad's project hues).
    private static readonly Color[] Hues =
    [
        Color.Parse("#818CF8"), Color.Parse("#34D399"), Color.Parse("#F472B6"),
        Color.Parse("#38BDF8"), Color.Parse("#A78BFA"), Color.Parse("#2DD4BF"),
    ];

    internal OnboardingProjectCandidate(DiscoveredProject project, bool isAdded, bool isSelected, DateTimeOffset now)
    {
        Project = project;
        _isAdded = isAdded;
        _isSelected = isSelected || isAdded;

        // A stable hash, so a project keeps its colour from one run to the next.
        var hash = project.Name.ToUpperInvariant().Aggregate(17, (sum, character) => unchecked(sum * 31 + character));
        var hue = Hues[(int)((uint)hash % (uint)Hues.Length)];
        BadgeForeground = new SolidColorBrush(hue).ToImmutable();
        BadgeBackground = new SolidColorBrush(Color.FromArgb(0x2E, hue.R, hue.G, hue.B)).ToImmutable();
        ActivityLabel = OnboardingText.Activity(project.LastActive, now);
    }

    internal DiscoveredProject Project { get; }

    public string Name => Project.Name;
    public string Path => Project.Path;
    public string Initial => LaunchpadProjectBadge.InitialOf(Project.Name);
    public IBrush BadgeForeground { get; }
    public IBrush BadgeBackground { get; }
    public bool IsFolder => Project.Kind == DiscoveredProjectKind.Folder;
    public string ActivityLabel { get; }
    public string? Description => Project.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Project.Description);

    /// <summary>The folder is a Lumi project already, so it can't be picked again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isAdded;

    public bool CanToggle => !IsAdded;

    /// <summary>What the project is, at a glance: its stack and repository, or how many documents it holds.</summary>
    public string Detail => IsFolder
        ? ManagementText.Count(Project.DocumentCount, Loc.Onboarding_DocumentsOne, Loc.Onboarding_DocumentsMany)
        : string.Join(" · ", new[] { Project.Stack, Project.Remote }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public bool HasDetail => Detail.Length > 0;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Toggle()
    {
        if (!IsAdded)
            IsSelected = !IsSelected;
    }
}

public enum OnboardingChatKind { Lumi, Typing, Question, Answer, Remembered }

/// <summary>One line of the getting-to-know-you conversation.</summary>
public sealed partial class OnboardingChatItem : ObservableObject
{
    private readonly Action<OnboardingChatItem, string>? _submit;

    private OnboardingChatItem(OnboardingChatKind kind, string text, OnboardingQuestion? question = null,
        Action<OnboardingChatItem, string>? submit = null)
    {
        Kind = kind;
        Text = text;
        Question = question;
        Options = question?.Options.ToList() ?? [];
        _submit = submit;
    }

    public OnboardingChatKind Kind { get; }
    public string Text { get; }
    internal OnboardingQuestion? Question { get; }

    /// <summary>The question card's choices (a list, because the card takes one).</summary>
    public List<string> Options { get; }

    public bool IsLumi => Kind == OnboardingChatKind.Lumi;
    public bool IsTyping => Kind == OnboardingChatKind.Typing;
    public bool IsQuestion => Kind == OnboardingChatKind.Question;
    public bool IsAnswer => Kind == OnboardingChatKind.Answer;
    public bool IsRemembered => Kind == OnboardingChatKind.Remembered;

    [ObservableProperty] private bool _isAnswered;
    [ObservableProperty] private bool _isExpired;

    /// <summary>Called by the question card when the user answers.</summary>
    public void Submit(string answer)
    {
        if (!IsQuestion || IsAnswered || IsExpired || string.IsNullOrWhiteSpace(answer))
            return;
        _submit?.Invoke(this, answer.Trim());
    }

    internal static OnboardingChatItem FromLumi(string text) => new(OnboardingChatKind.Lumi, text);
    internal static OnboardingChatItem Typing() => new(OnboardingChatKind.Typing, "");
    internal static OnboardingChatItem FromAnswer(string text) => new(OnboardingChatKind.Answer, text);
    internal static OnboardingChatItem FromMemory(string text) => new(OnboardingChatKind.Remembered, text);

    internal static OnboardingChatItem Ask(OnboardingQuestion question, Action<OnboardingChatItem, string> submit)
        => new(OnboardingChatKind.Question, question.Text, question, submit);
}

/// <summary>A memory onboarding saved, shown on the ready screen with a way to forget it.</summary>
public sealed partial class OnboardingMemoryItem : ObservableObject
{
    private readonly Action<OnboardingMemoryItem> _forget;

    internal OnboardingMemoryItem(Memory memory, Action<OnboardingMemoryItem> forget)
    {
        Memory = memory;
        _forget = forget;
    }

    internal Memory Memory { get; }
    public string Content => Memory.Content;

    [RelayCommand]
    private void Forget() => _forget(this);
}

/// <summary>A project onboarding set up, as the ready screen lists it.</summary>
public sealed class OnboardingProjectSummary
{
    internal OnboardingProjectSummary(Project project, OnboardingProjectCandidate candidate)
    {
        Project = project;
        Initial = candidate.Initial;
        BadgeForeground = candidate.BadgeForeground;
        BadgeBackground = candidate.BadgeBackground;
        Detail = candidate.Detail;
    }

    internal Project Project { get; }
    public string Name => Project.Name;
    public string Initial { get; }
    public IBrush BadgeForeground { get; }
    public IBrush BadgeBackground { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>A first task offered on the ready screen. Choosing it finishes onboarding and sends it.</summary>
public sealed partial class OnboardingStarterItem : ObservableObject
{
    private readonly Action<OnboardingStarterItem> _start;

    internal OnboardingStarterItem(OnboardingStarterIdea idea, Project? project, Action<OnboardingStarterItem> start)
    {
        Title = idea.Title;
        Prompt = idea.Prompt;
        Icon = idea.Icon;
        ProjectId = project?.Id;
        ProjectName = project?.Name ?? "";
        _start = start;
    }

    public string Title { get; }
    public string Prompt { get; }
    public string Icon { get; }
    public Guid? ProjectId { get; }
    public string ProjectName { get; }
    public bool HasProject => ProjectName.Length > 0;

    [RelayCommand]
    private void Start() => _start(this);
}

/// <summary>Small, localized phrasing helpers shared by the onboarding items.</summary>
internal static class OnboardingText
{
    public static string Activity(DateTimeOffset? lastActive, DateTimeOffset now)
    {
        if (lastActive is not { } active)
            return "";

        var days = (int)Math.Floor((now.Date - active.ToLocalTime().Date).TotalDays);
        return days switch
        {
            <= 0 => Loc.Onboarding_ActiveToday,
            1 => Loc.Onboarding_ActiveYesterday,
            < 14 => string.Format(Loc.Culture, Loc.Onboarding_ActiveDays, days),
            < 60 => string.Format(Loc.Culture, Loc.Onboarding_ActiveWeeks, days / 7),
            _ => string.Format(Loc.Culture, Loc.Onboarding_ActiveMonths, days / 30),
        };
    }
}
