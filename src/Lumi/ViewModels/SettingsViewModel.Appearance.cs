using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services.Appearance;

namespace Lumi.ViewModels;

/// <summary>One choice of a segmented setting (window material, presence brightness).</summary>
public sealed partial class SettingsChoice : ObservableObject
{
    public SettingsChoice(string id, string label, string? description, Action<string> select)
    {
        Id = id;
        Label = label;
        Description = description;
        SelectCommand = new RelayCommand(() => select(id));
    }

    public string Id { get; }
    public string Label { get; }
    public string? Description { get; }
    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>A swatch in the accent colour picker.</summary>
public sealed partial class AccentOption : ObservableObject
{
    public AccentOption(AccentPalettes.Palette palette, Action<string> select)
    {
        Palette = palette;
        Name = Loc.Get(palette.NameKey);
        SelectCommand = new RelayCommand(() => select(palette.Id));
    }

    public AccentPalettes.Palette Palette { get; }
    public string Id => Palette.Id;
    public string Name { get; }
    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private IBrush? _swatch;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A row of the keyboard shortcut reference: the keys, then what they do.</summary>
public sealed record KeyboardShortcutItem(IReadOnlyList<string> Keys, string Description);

public partial class SettingsViewModel
{
    public const string DangerClearChats = "chats";
    public const string DangerClearMemories = "memories";
    public const string DangerResetSettings = "settings";

    // ── Theme ──
    [ObservableProperty] private bool _followSystemTheme;
    private bool _isApplyingSystemTheme;

    /// <summary>Reads the OS light/dark preference; replaceable so tests can stand in for the platform.</summary>
    internal Func<bool?> SystemPrefersDark { get; set; } = () => SystemTheme.PrefersDark();

    public bool IsLightThemeSelected => !FollowSystemTheme && !IsDarkTheme;
    public bool IsDarkThemeSelected => !FollowSystemTheme && IsDarkTheme;
    public bool IsSystemThemeSelected => FollowSystemTheme;

    [RelayCommand]
    private void SelectTheme(string? mode)
    {
        // The saved setting decides, not this window's copy: another Settings window may have
        // changed the theme mode since this one opened.
        var settings = _dataStore.Data.Settings;
        switch (mode)
        {
            case "system":
                if (FollowSystemTheme)
                {
                    settings.FollowSystemTheme = true;
                    ApplySystemTheme(SystemPrefersDark());
                    Save();
                }
                else
                {
                    FollowSystemTheme = true;
                }
                break;
            default:
                FollowSystemTheme = false;
                settings.FollowSystemTheme = false;
                ShowTheme(dark: mode != "light");
                Save();
                break;
        }
    }

    partial void OnFollowSystemThemeChanged(bool value)
    {
        _dataStore.Data.Settings.FollowSystemTheme = value;
        if (value)
            ApplySystemTheme(SystemPrefersDark());
        Save();
        NotifyThemeSelectionChanged();
        NotifyModified();
    }

    /// <summary>Follows the OS into light or dark while "System" is the saved theme mode.</summary>
    public void ApplySystemTheme(bool? prefersDark)
    {
        if (!_dataStore.Data.Settings.FollowSystemTheme || prefersDark is not { } dark)
            return;

        _isApplyingSystemTheme = true;
        try
        {
            ShowTheme(dark);
        }
        finally
        {
            _isApplyingSystemTheme = false;
        }
    }

    // This window's copy can be stale: another window may have changed the theme since. If the copy
    // already reads the requested theme, it first catches up to the one on screen (no visible change),
    // so setting the request still reaches the app.
    private void ShowTheme(bool dark)
    {
        if (IsDarkTheme == dark && _dataStore.Data.Settings.IsDarkTheme != dark)
            IsDarkTheme = !dark;
        IsDarkTheme = dark;
    }

    private void NotifyThemeSelectionChanged()
    {
        OnPropertyChanged(nameof(IsLightThemeSelected));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
        OnPropertyChanged(nameof(IsSystemThemeSelected));
    }

    // ── Accent ──
    [ObservableProperty] private string _accentColor = AccentPalettes.DefaultId;

    public ObservableCollection<AccentOption> AccentOptions { get; } = [];

    partial void OnAccentColorChanged(string value)
    {
        var normalized = AccentPalettes.Normalize(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            AccentColor = normalized;
            return;
        }

        SyncAccentSelection();
        if (_isSynchronizingAppearance)
            return;

        _dataStore.Data.Settings.AccentColor = value;
        if (Avalonia.Application.Current is { } application)
        {
            AccentPalettes.Apply(application.Resources, value);
            (application as App)?.RefreshTrayIcon();
        }
        _dataStore.NotifyAppearanceSettingsChanged(this);
        Save();
        NotifyModified();
    }

    private void InitializeAppearanceOptions()
    {
        foreach (var palette in AccentPalettes.All)
            AccentOptions.Add(new AccentOption(palette, id => AccentColor = id));
        RefreshAccentSwatches();
        SyncAccentSelection();

        foreach (var material in WindowMaterials.Available)
        {
            var (label, description) = material switch
            {
                UserSettings.WindowMaterialMica => (Loc.Material_Mica, Loc.Material_MicaDesc),
                UserSettings.WindowMaterialSolid => (Loc.Material_Solid, Loc.Material_SolidDesc),
                _ when OperatingSystem.IsWindows() => (Loc.Material_Acrylic, Loc.Material_AcrylicDesc),
                _ => (Loc.Material_Translucent, Loc.Material_TranslucentDesc),
            };
            WindowMaterialOptions.Add(new SettingsChoice(material, label, description, id => WindowMaterial = id));
        }
        SyncChoices(WindowMaterialOptions, WindowMaterial);

        PresenceBrightnessOptions.Add(new SettingsChoice(UserSettings.PresenceBrightnessSubtle, Loc.Presence_Subtle, null, id => PresenceBrightness = id));
        PresenceBrightnessOptions.Add(new SettingsChoice(UserSettings.PresenceBrightnessBalanced, Loc.Presence_Balanced, null, id => PresenceBrightness = id));
        PresenceBrightnessOptions.Add(new SettingsChoice(UserSettings.PresenceBrightnessVivid, Loc.Presence_Vivid, null, id => PresenceBrightness = id));
        SyncChoices(PresenceBrightnessOptions, PresenceBrightness);
    }

    /// <summary>Swatches show each palette as it looks in the current theme.</summary>
    private void RefreshAccentSwatches()
    {
        foreach (var option in AccentOptions)
            option.Swatch = AccentPalettes.CreateSwatch(option.Palette, IsDarkTheme);
    }

    private void SyncAccentSelection()
    {
        foreach (var option in AccentOptions)
            option.IsSelected = option.Id == AccentColor;
    }

    // ── Window material ──
    [ObservableProperty] private string _windowMaterial = UserSettings.WindowMaterialAuto;

    public ObservableCollection<SettingsChoice> WindowMaterialOptions { get; } = [];

    partial void OnWindowMaterialChanged(string value)
    {
        var normalized = WindowMaterials.Normalize(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            WindowMaterial = normalized;
            return;
        }

        SyncChoices(WindowMaterialOptions, value);
        if (_isSynchronizingAppearance)
            return;

        _dataStore.Data.Settings.WindowMaterial = value;
        if (Avalonia.Application.Current is App app)
            app.ApplyWindowMaterial(value);
        _dataStore.NotifyAppearanceSettingsChanged(this);
        Save();
        NotifyModified();
    }

    // ── Presence brightness ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewGlowOpacity))]
    private string _presenceBrightness = UserSettings.PresenceBrightnessBalanced;

    public ObservableCollection<SettingsChoice> PresenceBrightnessOptions { get; } = [];

    /// <summary>How strongly the Appearance preview glows: off with the presence, brighter when vivid.</summary>
    public double PreviewGlowOpacity => ShowAmbientPresence
        ? Math.Min(0.9, 0.45 * PresenceBrightnessMultiplier(PresenceBrightness))
        : 0;

    /// <summary>Raised with the new glow multiplier so live chat surfaces can follow.</summary>
    public event Action<double>? PresenceBrightnessChanged;

    /// <summary>How much brighter or quieter than Lumi's tuned glow the presence shines.</summary>
    public static double PresenceBrightnessMultiplier(string? brightness) => brightness switch
    {
        UserSettings.PresenceBrightnessSubtle => 0.6,
        UserSettings.PresenceBrightnessVivid => 1.45,
        _ => 1.0,
    };

    private static string NormalizePresenceBrightness(string? brightness) => brightness switch
    {
        UserSettings.PresenceBrightnessSubtle or UserSettings.PresenceBrightnessVivid => brightness,
        _ => UserSettings.PresenceBrightnessBalanced,
    };

    partial void OnPresenceBrightnessChanged(string value)
    {
        var normalized = NormalizePresenceBrightness(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            PresenceBrightness = normalized;
            return;
        }

        SyncChoices(PresenceBrightnessOptions, value);
        if (_isSynchronizingAppearance)
            return;

        _dataStore.Data.Settings.PresenceBrightness = value;
        PresenceBrightnessChanged?.Invoke(PresenceBrightnessMultiplier(value));
        _dataStore.NotifyAppearanceSettingsChanged(this);
        Save();
        NotifyModified();
    }

    private static void SyncChoices(IEnumerable<SettingsChoice> choices, string selectedId)
    {
        foreach (var choice in choices)
            choice.IsSelected = choice.Id == selectedId;
    }

    // ── Small segmented choices ──
    public bool IsComfortableDensity => !IsCompactDensity;
    public bool IsSendWithCtrlEnter => !SendWithEnter;
    public bool IsSexMale => UserSexIndex == 0;
    public bool IsSexFemale => UserSexIndex == 1;
    public bool IsSexUnspecified => UserSexIndex is not (0 or 1);

    [RelayCommand]
    private void SelectDensity(string? density) => IsCompactDensity = density == "compact";

    [RelayCommand]
    private void SelectSendKey(string? key) => SendWithEnter = key != "ctrl-enter";

    [RelayCommand]
    private void SelectSex(string? sex) => UserSexIndex = sex switch { "male" => 0, "female" => 1, _ => 2 };

    /// <summary>Puts every look-and-feel choice back to Lumi's defaults, leaving chat and AI settings alone.</summary>
    [RelayCommand]
    private void ResetAppearance()
    {
        SelectTheme(_defaults.IsDarkTheme ? "dark" : "light");
        AccentColor = _defaults.AccentColor;
        WindowMaterial = _defaults.WindowMaterial;
        IsCompactDensity = _defaults.IsCompactDensity;
        UiScalePercent = _defaults.UiScalePercent;
        ShowAmbientPresence = _defaults.ShowAmbientPresence;
        PresenceBrightness = _defaults.PresenceBrightness;
        AnimatePresenceWhileWorking = _defaults.AnimatePresenceWhileWorking;
        ShowAnimations = _defaults.ShowAnimations;
    }

    public bool IsAppearanceCustomized => IsThemeModified
        || IsAccentColorModified
        || IsWindowMaterialModified
        || IsCompactDensityModified
        || IsUiScaleModified
        || IsShowAmbientPresenceModified
        || IsPresenceBrightnessModified
        || IsAnimatePresenceWhileWorkingModified
        || IsShowAnimationsModified;

    // ── Profile ──
    public string ProfileInitial
    {
        get
        {
            var name = UserName?.Trim();
            return string.IsNullOrEmpty(name)
                ? ""
                : char.ToUpper(name[0], Loc.Culture).ToString();
        }
    }

    public bool HasProfileInitial => ProfileInitial.Length > 0;

    public string ProfileDisplayName => string.IsNullOrWhiteSpace(UserName)
        ? Loc.Settings_ProfileNoName
        : UserName.Trim();

    public string ProfileGreeting => string.IsNullOrWhiteSpace(UserName)
        ? Loc.Settings_ProfileGreetingNoName
        : string.Format(Loc.Culture, Loc.Settings_ProfileGreeting, UserName.Trim());

    private void NotifyProfileChanged()
    {
        OnPropertyChanged(nameof(ProfileInitial));
        OnPropertyChanged(nameof(HasProfileInitial));
        OnPropertyChanged(nameof(ProfileDisplayName));
        OnPropertyChanged(nameof(ProfileGreeting));
    }

    // ── Destructive actions ask once, in the floating bar ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingDanger))]
    [NotifyPropertyChangedFor(nameof(DangerConfirmText))]
    [NotifyPropertyChangedFor(nameof(DangerConfirmActionText))]
    private string? _pendingDangerAction;

    public bool IsConfirmingDanger => PendingDangerAction is not null;

    public string DangerConfirmText => PendingDangerAction switch
    {
        DangerClearChats => string.Format(Loc.Culture, Loc.Settings_ConfirmClearChats, TotalChats),
        DangerClearMemories => string.Format(Loc.Culture, Loc.Settings_ConfirmClearMemories, TotalMemories),
        DangerResetSettings => Loc.Settings_ConfirmResetSettings,
        _ => "",
    };

    public string DangerConfirmActionText => PendingDangerAction switch
    {
        DangerClearChats => Loc.Settings_ConfirmClearChatsAction,
        DangerClearMemories => Loc.Settings_ConfirmClearMemoriesAction,
        DangerResetSettings => Loc.Settings_ConfirmResetSettingsAction,
        _ => "",
    };

    [RelayCommand]
    private void RequestDangerAction(string? action)
        => PendingDangerAction = action is DangerClearChats or DangerClearMemories or DangerResetSettings ? action : null;

    [RelayCommand]
    private void CancelDangerAction() => PendingDangerAction = null;

    [RelayCommand]
    private void ConfirmDangerAction()
    {
        var action = PendingDangerAction;
        PendingDangerAction = null;
        switch (action)
        {
            case DangerClearChats:
                _ = ClearAllChatsAsync();
                break;
            case DangerClearMemories:
                ClearAllMemories();
                break;
            case DangerResetSettings:
                ResetSettings();
                break;
        }
        RefreshStats();
    }

    // ── Keyboard shortcuts, for reference ──
    public IReadOnlyList<KeyboardShortcutItem> KeyboardShortcuts { get; } = BuildKeyboardShortcuts();

    private static IReadOnlyList<KeyboardShortcutItem> BuildKeyboardShortcuts()
    {
        var mac = OperatingSystem.IsMacOS();
        var command = mac ? "⌘" : "Ctrl";
        var shift = mac ? "⇧" : "Shift";
        var alt = mac ? "⌥" : "Alt";

        return
        [
            new([command, "K"], Loc.Shortcut_Search),
            new([command, "N"], Loc.Shortcut_NewChat),
            new([command, "L"], Loc.Shortcut_FocusComposer),
            new([command, shift, "N"], Loc.Shortcut_NewWindow),
            new([command, shift, "D"], Loc.Shortcut_Duplicate),
            new([command, "B"], Loc.Shortcut_ToggleSidebar),
            new([command, "1–5"], Loc.Shortcut_Places),
            new([command, ","], Loc.Shortcut_Settings),
            new([command, "+ / −"], Loc.Shortcut_Scale),
            new([alt, "← / →"], Loc.Shortcut_History),
        ];
    }
}
