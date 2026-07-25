using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GitHub.Copilot;
using Lumi.Services;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Covers the pure pieces of the on-demand model catalog refresh: the fingerprint that decides
/// whether a refetched catalog is worth publishing, and the minimal-diff collection sync that keeps
/// an open model picker stable while the list is updated.
/// </summary>
public class ModelCatalogRefreshTests
{
    private static ModelContextWindowCatalog Catalog(
        IEnumerable<string>? longContextModelIds = null,
        IDictionary<string, ModelContextWindowLimits>? limits = null)
        => new(
            new HashSet<string>(longContextModelIds ?? [], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ModelContextWindowLimits>(
                limits ?? new Dictionary<string, ModelContextWindowLimits>(),
                StringComparer.OrdinalIgnoreCase));

    private static ModelInfo Model(string id, IList<string>? efforts = null, string? defaultEffort = null)
        => new()
        {
            Id = id,
            Name = id,
            SupportedReasoningEfforts = efforts,
            DefaultReasoningEffort = defaultEffort
        };

    [Fact]
    public void Signature_IsStable_ForSameCatalogInDifferentOrder()
    {
        var first = ModelCatalogCache.BuildSignature(
            [Model("gpt-5.4", ["low", "high"], "high"), Model("claude-opus-5")],
            Catalog(["claude-opus-5"]));
        var second = ModelCatalogCache.BuildSignature(
            [Model("claude-opus-5"), Model("gpt-5.4", ["high", "low"], "high")],
            Catalog(["claude-opus-5"]));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Signature_Changes_WhenNewModelAppears()
    {
        var before = ModelCatalogCache.BuildSignature([Model("gpt-5.4")], Catalog());
        var after = ModelCatalogCache.BuildSignature(
            [Model("gpt-5.4"), Model("claude-opus-5")],
            Catalog());

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Signature_Changes_WhenCapabilitiesChangeForSameModel()
    {
        var before = ModelCatalogCache.BuildSignature(
            [Model("gpt-5.4", ["low", "high"], "high")],
            Catalog());
        var afterEfforts = ModelCatalogCache.BuildSignature(
            [Model("gpt-5.4", ["low", "high", "max"], "high")],
            Catalog());
        var afterLongContext = ModelCatalogCache.BuildSignature(
            [Model("gpt-5.4", ["low", "high"], "high")],
            Catalog(["gpt-5.4"]));

        Assert.NotEqual(before, afterEfforts);
        Assert.NotEqual(before, afterLongContext);
    }

    /// <summary>
    /// Guards the reason <see cref="CopilotService"/> must never commit a failed context-window
    /// fetch: an empty catalog is not signature-equivalent to a populated one, so publishing it
    /// would be broadcast as a genuine change and silently strip long context from every model.
    /// </summary>
    [Fact]
    public void Signature_TreatsEmptyContextWindowCatalogAsADifferentCatalog()
    {
        var models = new[] { Model("gpt-5.4"), Model("claude-opus-5") };
        var populated = ModelCatalogCache.BuildSignature(
            models,
            Catalog(
                ["claude-opus-5"],
                new Dictionary<string, ModelContextWindowLimits>
                {
                    ["claude-opus-5"] = new(128_000, 1_000_000)
                }));
        var empty = ModelCatalogCache.BuildSignature(models, Catalog());

        Assert.NotEqual(populated, empty);
    }

    [Fact]
    public void SyncAvailableModels_ReportsNoChange_WhenCatalogIsIdentical()
    {
        var target = new ObservableCollection<string> { "gpt-5.4", "claude-opus-5" };
        var changes = 0;
        target.CollectionChanged += (_, _) => changes++;

        var changed = ModelSelectionHelper.SyncAvailableModels(target, ["gpt-5.4", "claude-opus-5"], "gpt-5.4");

        Assert.False(changed);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void SyncAvailableModels_AddsNewModelWithoutResettingCollection()
    {
        var target = new ObservableCollection<string> { "gpt-5.4", "claude-opus-5" };
        var resets = 0;
        target.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                resets++;
        };

        var changed = ModelSelectionHelper.SyncAvailableModels(
            target,
            ["gpt-5.4", "claude-opus-5", "claude-opus-6"],
            "gpt-5.4");

        Assert.True(changed);
        Assert.Equal(0, resets);
        Assert.Equal(["gpt-5.4", "claude-opus-5", "claude-opus-6"], target);
    }

    [Fact]
    public void SyncAvailableModels_KeepsPinnedSelectionThatLeftTheCatalog()
    {
        var target = new ObservableCollection<string> { "gpt-5.4", "retired-model", "claude-opus-5" };

        var changed = ModelSelectionHelper.SyncAvailableModels(target, ["gpt-5.4", "claude-opus-5"], "retired-model");

        Assert.True(changed);
        Assert.Equal(["gpt-5.4", "claude-opus-5", "retired-model"], target);
    }

    [Fact]
    public void SyncAvailableModels_DropsRetiredModelWhenItIsNotPinned()
    {
        var target = new ObservableCollection<string> { "gpt-5.4", "retired-model" };

        var changed = ModelSelectionHelper.SyncAvailableModels(target, ["gpt-5.4"], pinnedModel: null);

        Assert.True(changed);
        Assert.Equal(["gpt-5.4"], target);
    }

    [Fact]
    public void SyncAvailableModels_ReordersToMatchCatalogOrder()
    {
        var target = new ObservableCollection<string> { "claude-opus-5", "gpt-5.4" };

        var changed = ModelSelectionHelper.SyncAvailableModels(target, ["gpt-5.4", "claude-opus-5"], null);

        Assert.True(changed);
        Assert.Equal(["gpt-5.4", "claude-opus-5"], target);
    }
}
