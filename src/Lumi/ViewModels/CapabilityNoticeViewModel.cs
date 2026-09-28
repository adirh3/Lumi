using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Services;
using Lumi.Services.Sharing;

namespace Lumi.ViewModels;

/// <summary>
/// The small notice at the top of the window for sharing: a confirmation after "Copy for chat", and
/// the offer that makes receiving effortless — when you come back to Lumi with a Lumi code (or pack)
/// on the clipboard, it says what it is and offers a preview.
///
/// <para>Detection is deliberately modest: it runs when the window is activated and the clipboard has
/// changed, looks only for Lumi's own formats, never offers what this Lumi copied itself, what you
/// already have, or what you already declined, and keeps nothing but a hash of the last clipboard
/// text in memory. It only ever opens the import preview; nothing is added without it.</para>
/// </summary>
public partial class CapabilityNoticeViewModel : ObservableObject
{
    private static readonly object OwnCopiesSync = new();
    private static readonly HashSet<int> OwnCopies = [];

    private readonly DataStore _dataStore;
    private readonly Func<Task<string?>> _readClipboard;
    private readonly Func<bool> _canOffer;
    private readonly HashSet<int> _handled = [];
    private int? _lastClipboardHash;
    private int? _offerHash;
    private string? _offerText;
    private int _generation;
    private bool _isChecking;

    public CapabilityNoticeViewModel(DataStore dataStore, Func<Task<string?>> readClipboard, Func<bool> canOffer)
    {
        _dataStore = dataStore;
        _readClipboard = readClipboard;
        _canOffer = canOffer;
    }

    [ObservableProperty] private bool _isShown;
    [ObservableProperty] private bool _isOffer;

    /// <summary>The capability the notice is about; its glyph and colours identify it at a glance.</summary>
    [ObservableProperty] private CapabilityCardViewModel? _card;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";

    /// <summary>The user wants to see what the copied capability contains.</summary>
    public event Action<string>? PreviewRequested;

    /// <summary>Remembers text Lumi itself put on the clipboard, so the sharer is not offered their own share.</summary>
    public static void RememberOwnCopy(string text)
    {
        lock (OwnCopiesSync)
            OwnCopies.Add(Hash(text));
    }

    private static bool IsOwnCopy(int hash)
    {
        lock (OwnCopiesSync)
            return OwnCopies.Contains(hash);
    }

    private static int Hash(string text) => PackText.NormalizeNewlines(text).Trim().GetHashCode(StringComparison.Ordinal);

    public async Task CheckClipboardAsync()
    {
        if (_isChecking || !_dataStore.Data.Settings.OfferCopiedCapabilities || !_canOffer())
            return;

        _isChecking = true;
        try
        {
            string? text;
            try
            {
                text = await _readClipboard();
            }
            catch
            {
                text = null;
            }

            var hash = string.IsNullOrWhiteSpace(text) || text.Length > CapabilityPackReader.MaxTextLength
                ? (int?)null
                : Hash(text);
            if (hash == _lastClipboardHash)
                return;
            _lastClipboardHash = hash;

            // The clipboard moved on, so an offer for what used to be on it no longer applies.
            if (IsShown && IsOffer && hash != _offerHash)
                IsShown = false;

            if (hash is not { } current || IsOwnCopy(current) || _handled.Contains(current) || !LooksLikeLumiCapability(text!))
                return;

            // Decoding is bounded, but it is still work that has no place on the UI thread.
            var result = await Task.Run(() => CapabilityPackReader.Read(text));
            if (!result.Success || hash != _lastClipboardHash || !_canOffer() || IsAlreadyInLibrary(result.Pack!))
                return;

            var card = CapabilityCardViewModel.ForPack(result.Pack!, "");
            _offerText = text;
            _offerHash = current;
            _generation++;
            Card = card;
            Title = string.Format(CultureInfo.CurrentCulture, Loc.Notice_OfferTitle, card.Name);
            Detail = card.KindLabel + " · " + card.ContentsLabel;
            IsOffer = true;
            IsShown = true;
        }
        finally
        {
            _isChecking = false;
        }
    }

    /// <summary>
    /// Nothing to offer: every part is already here. A Lumi is always imported as a new copy, so one
    /// with the same name and instructions counts as already here too (the sharer copying their own
    /// Lumi back out of a chat, for example).
    /// </summary>
    private bool IsAlreadyInLibrary(CapabilityPack pack)
    {
        if (CapabilityImporter.Plan(pack, _dataStore).NewItemCount == 0)
            return true;

        return pack.Lumi is { } lumi
               && _dataStore.Data.Agents.Any(agent =>
                   string.Equals(agent.Name.Trim(), lumi.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                   && PackText.NormalizeNewlines(agent.SystemPrompt).Trim() == PackText.NormalizeNewlines(lumi.SystemPrompt).Trim());
    }

    /// <summary>Lumi codes and Lumi packs only. A plain SKILL.md or MCP config could be on the clipboard for any reason.</summary>
    internal static bool LooksLikeLumiCapability(string text)
    {
        if (ShareCode.Contains(text))
            return true;

        var trimmed = text.TrimStart();
        return trimmed.StartsWith("---", StringComparison.Ordinal)
               && trimmed.AsSpan(0, Math.Min(trimmed.Length, 400)).Contains("lumi-pack:", StringComparison.Ordinal);
    }

    /// <summary>A brief confirmation that hides itself.</summary>
    public void ShowCopied(CapabilityCardViewModel card)
    {
        var generation = ++_generation;
        Card = card;
        Title = string.Format(CultureInfo.CurrentCulture, Loc.Notice_CopiedTitle, card.Name);
        Detail = Loc.Notice_CopiedDetail;
        IsOffer = false;
        IsShown = true;
        _ = HideLaterAsync(generation);
    }

    private async Task HideLaterAsync(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (generation == _generation)
            IsShown = false;
    }

    [RelayCommand]
    private void Preview()
    {
        if (_offerText is not { } text)
            return;

        if (_offerHash is { } hash)
            _handled.Add(hash);
        IsShown = false;
        PreviewRequested?.Invoke(text);
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (IsOffer && _offerHash is { } hash)
            _handled.Add(hash);
        IsShown = false;
    }
}
