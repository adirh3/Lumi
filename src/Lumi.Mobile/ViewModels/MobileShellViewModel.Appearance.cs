using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Mobile.Appearance;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

/// <summary>A text size of the picker, drawn as "Aa" at a size that grows with it.</summary>
public sealed partial class TextSizeChoice : ObservableObject
{
    public TextSizeChoice(int percent, string label, Action select)
    {
        Percent = percent;
        Label = label;
        SampleSize = Math.Round(16 * percent / 100.0, 1);
        SelectCommand = new RelayCommand(select);
    }

    public int Percent { get; }

    public string Label { get; }

    /// <summary>The picker's sample at this size: the body rung, scaled.</summary>
    public double SampleSize { get; }

    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>A swatch of the accent picker.</summary>
public sealed partial class AccentChoice : ObservableObject
{
    public AccentChoice(MobileAccentPalettes.Palette palette, Action<string> select)
    {
        Palette = palette;
        Name = Strings.Get(palette.NameKey);
        SelectCommand = new RelayCommand(() => select(palette.Id));
    }

    public MobileAccentPalettes.Palette Palette { get; }

    public string Id => Palette.Id;

    public string Name { get; }

    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Settings → Appearance: how Lumi looks on this phone, as the desktop's Appearance page makes Lumi the
/// user's own. Each choice is remembered with the phone's other settings and shows at once; the
/// application applies the accent and the text size (<see cref="App"/>), the chat its glow.
/// </summary>
public sealed partial class MobileShellViewModel
{
    /// <summary>How brightly Lumi's glow shines behind the chat, as the desktop's presence brightness.</summary>
    public enum GlowPreference
    {
        Off,
        Subtle,
        Balanced,
        Vivid
    }

    public const int DefaultTextSize = 100;

    /// <summary>Lumi's text sizes, in percent of the size the phone draws text at.</summary>
    public static IReadOnlyList<int> TextSizes { get; } = [90, 100, 112, 125, 140];

    [ObservableProperty] private string _accentColor = MobileAccentPalettes.DefaultId;

    [ObservableProperty] private int _textSize = DefaultTextSize;

    [ObservableProperty] private GlowPreference _glow = GlowPreference.Balanced;

    [ObservableProperty] private bool _animateGlowWhileWorking;

    public IReadOnlyList<AccentChoice> AccentChoices { get; private set; } = [];

    public IReadOnlyList<TextSizeChoice> TextSizeChoices { get; private set; } = [];

    /// <summary>The chosen palette's name, beside the accent picker and in the Settings list.</summary>
    public string AccentName => Strings.Get(MobileAccentPalettes.Find(AccentColor).NameKey);

    public string TextSizeLabel => TextSizeChoices.FirstOrDefault(choice => choice.Percent == TextSize)?.Label ?? "";

    public string GlowLabel => Strings.Get(GlowKey(Glow));

    /// <summary>The look in words, beside Appearance in the Settings list: the theme, then the accent.</summary>
    public string AppearanceSummary => $"{ThemeLabel} · {AccentName}";

    /// <summary>How much brighter or quieter than its tuned level the glow shines; the desktop's multipliers.</summary>
    public static double GlowMultiplierFor(GlowPreference glow) => glow switch
    {
        GlowPreference.Off => 0,
        GlowPreference.Subtle => 0.6,
        GlowPreference.Vivid => 1.45,
        _ => 1.0
    };

    public double GlowMultiplier => GlowMultiplierFor(Glow);

    public bool IsGlowOn => Glow != GlowPreference.Off;

    /// <summary>Selected-state helpers for the glow's segments.</summary>
    public bool IsGlowOff => Glow == GlowPreference.Off;

    public bool IsGlowSubtle => Glow == GlowPreference.Subtle;

    public bool IsGlowBalanced => Glow == GlowPreference.Balanced;

    public bool IsGlowVivid => Glow == GlowPreference.Vivid;

    /// <summary>Whether any look-and-feel choice differs from Lumi's own; the language is not a look.</summary>
    public bool IsAppearanceCustomized =>
        Theme != ThemePreference.System
        || AccentColor != MobileAccentPalettes.DefaultId
        || TextSize != DefaultTextSize
        || Glow != GlowPreference.Balanced
        || AnimateGlowWhileWorking;

    /// <summary>Reads the saved look, falling back to Lumi's own for anything a newer or older build wrote.</summary>
    private void LoadAppearance()
    {
        AccentChoices = MobileAccentPalettes.All
            .Select(palette => new AccentChoice(palette, id => AccentColor = id))
            .ToArray();
        TextSizeChoices = TextSizes
            .Select(size => new TextSizeChoice(size, Strings.Get(TextSizeKey(size)), () => TextSize = size))
            .ToArray();

        AccentColor = MobileAccentPalettes.Normalize(_settings.AccentColor);
        TextSize = TextSizes.Contains(_settings.TextSize) ? _settings.TextSize : DefaultTextSize;
        Glow = Enum.TryParse<GlowPreference>(_settings.Glow, ignoreCase: true, out var glow) && Enum.IsDefined(glow)
            ? glow
            : GlowPreference.Balanced;
        AnimateGlowWhileWorking = _settings.AnimateGlowWhileWorking;
        SyncAppearanceChoices();
    }

    private static string TextSizeKey(int size) => size switch
    {
        < DefaultTextSize => "TextSizeSmall",
        DefaultTextSize => "TextSizeDefault",
        <= 112 => "TextSizeLarge",
        <= 125 => "TextSizeLarger",
        _ => "TextSizeLargest"
    };

    private static string GlowKey(GlowPreference glow) => glow switch
    {
        GlowPreference.Off => "Off",
        GlowPreference.Subtle => "GlowSubtle",
        GlowPreference.Vivid => "GlowVivid",
        _ => "GlowBalanced"
    };

    private void SyncAppearanceChoices()
    {
        foreach (var choice in AccentChoices)
            choice.IsSelected = choice.Id == AccentColor;
        foreach (var choice in TextSizeChoices)
            choice.IsSelected = choice.Percent == TextSize;
    }

    partial void OnAccentColorChanged(string value)
    {
        var normalized = MobileAccentPalettes.Normalize(value);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            AccentColor = normalized;
            return;
        }

        _settings.AccentColor = value;
        _store.Save(_settings);
        SyncAppearanceChoices();
        OnPropertyChanged(nameof(AccentName));
        OnAppearanceChanged();
    }

    partial void OnTextSizeChanged(int value)
    {
        if (!TextSizes.Contains(value))
        {
            TextSize = DefaultTextSize;
            return;
        }

        _settings.TextSize = value;
        _store.Save(_settings);
        SyncAppearanceChoices();
        OnPropertyChanged(nameof(TextSizeLabel));
        OnAppearanceChanged();
    }

    partial void OnGlowChanged(GlowPreference value)
    {
        _settings.Glow = value.ToString();
        _store.Save(_settings);
        OnPropertyChanged(nameof(GlowLabel));
        OnPropertyChanged(nameof(GlowMultiplier));
        OnPropertyChanged(nameof(IsGlowOn));
        OnPropertyChanged(nameof(IsGlowOff));
        OnPropertyChanged(nameof(IsGlowSubtle));
        OnPropertyChanged(nameof(IsGlowBalanced));
        OnPropertyChanged(nameof(IsGlowVivid));
        OnAppearanceChanged();
    }

    [RelayCommand]
    private void SelectGlow(string? glow)
    {
        if (Enum.TryParse<GlowPreference>(glow, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            Glow = parsed;
    }

    partial void OnAnimateGlowWhileWorkingChanged(bool value)
    {
        _settings.AnimateGlowWhileWorking = value;
        _store.Save(_settings);
        OnAppearanceChanged();
    }

    private void OnAppearanceChanged()
    {
        OnPropertyChanged(nameof(AppearanceSummary));
        OnPropertyChanged(nameof(IsAppearanceCustomized));
    }

    /// <summary>Puts every look-and-feel choice back to Lumi's own, leaving the language alone.</summary>
    [RelayCommand]
    private void RestoreDefaultLook()
    {
        Theme = ThemePreference.System;
        AccentColor = MobileAccentPalettes.DefaultId;
        TextSize = DefaultTextSize;
        Glow = GlowPreference.Balanced;
        AnimateGlowWhileWorking = false;
    }
}
