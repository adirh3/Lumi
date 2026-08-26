using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// The composer's model picker renders rich rows and lets the user pin favorites. These cover the
/// two things that can silently break: the badge metadata mapping/ordering, and the fact that
/// selecting a rich row must still write a plain model id back to <c>SelectedModel</c>.
/// </summary>
public class ModelOptionCatalogTests
{
    [Fact]
    public void OrderPinnedFirst_PutsFavoritesOnTopInPinOrder()
    {
        var ordered = ModelOptionCatalog.OrderPinnedFirst(
            ["auto", "gpt-5.4", "claude-opus-5", "gemini-3.1-pro"],
            ["claude-opus-5", "auto"]);

        Assert.Equal(["claude-opus-5", "auto", "gpt-5.4", "gemini-3.1-pro"], ordered);
    }

    [Fact]
    public void OrderPinnedFirst_KeepsCatalogOrderWhenNothingIsPinned()
    {
        var ordered = ModelOptionCatalog.OrderPinnedFirst(["auto", "gpt-5.4"], []);

        Assert.Equal(["auto", "gpt-5.4"], ordered);
    }

    // A favorite can outlive the model: entitlements change and BYOK tokens come and go. The picker
    // must not invent a row for a model the catalog no longer offers.
    [Fact]
    public void OrderPinnedFirst_IgnoresFavoritesMissingFromTheCatalog()
    {
        var ordered = ModelOptionCatalog.OrderPinnedFirst(["auto"], ["retired-model", "auto"]);

        Assert.Equal(["auto"], ordered);
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(900L, "900")]
    [InlineData(128_000L, "128K")]
    [InlineData(200_000L, "200K")]
    [InlineData(1_000_000L, "1M")]
    [InlineData(1_048_576L, "1M")]
    [InlineData(1_500_000L, "1.5M")]
    public void FormatContextTokens_RendersCompactBadges(long tokens, string? expected)
        => Assert.Equal(expected, ModelOptionCatalog.FormatContextTokens(tokens));

    [Fact]
    public void FormatCostMultiplier_RendersMultiplierAndFreeTier()
    {
        Assert.Equal("1\u00d7", ModelOptionCatalog.FormatCostMultiplier(1));
        Assert.Equal("0.33\u00d7", ModelOptionCatalog.FormatCostMultiplier(0.33));
        Assert.Equal("Included", ModelOptionCatalog.FormatCostMultiplier(0));
        Assert.Null(ModelOptionCatalog.FormatCostMultiplier(null));
    }

    [Fact]
    public void Describe_ReadsOnlyMetadataTheSdkActuallyReports()
    {
        var metadata = ModelOptionCatalog.Describe(new ModelInfo
        {
            Id = "claude-opus-5",
            Capabilities = new ModelCapabilities
            {
                Limits = new ModelLimits { MaxContextWindowTokens = 200_000 },
                Supports = new ModelSupports { Vision = true, ReasoningEffort = true }
            },
            Billing = new ModelBilling { Multiplier = 1 },
            Policy = new ModelPolicy { State = "enabled" }
        });

        Assert.Equal(200_000, metadata.ContextTokens);
        Assert.Equal(1, metadata.CostMultiplier);
        Assert.True(metadata.SupportsVision);
        Assert.True(metadata.SupportsReasoning);
        Assert.False(metadata.RequiresOptIn);
    }

    // BYOK tokens are pushed through the same path as `new ModelInfo { Id = token }`, with no
    // capabilities, policy or billing at all. Describing one must not throw or claim capabilities.
    [Fact]
    public void Describe_ToleratesAModelWithNoCapabilityMetadata()
    {
        var metadata = ModelOptionCatalog.Describe(new ModelInfo { Id = "byok:endpoint/model" });

        Assert.Equal(0, metadata.ContextTokens);
        Assert.Null(metadata.CostMultiplier);
        Assert.False(metadata.SupportsVision);
        Assert.False(metadata.SupportsReasoning);
        Assert.False(metadata.RequiresOptIn);
    }

    [Fact]
    public void Describe_FlagsAModelWhosePolicyStillNeedsAccepting()
    {
        var metadata = ModelOptionCatalog.Describe(new ModelInfo
        {
            Id = "gated-model",
            Policy = new ModelPolicy { State = "unconfigured" }
        });

        Assert.True(metadata.RequiresOptIn);
    }

    // A model that lists reasoning efforts but leaves the capability flag unset still reasons.
    [Fact]
    public void Describe_InfersReasoningFromTheSupportedEffortList()
    {
        var metadata = ModelOptionCatalog.Describe(new ModelInfo
        {
            Id = "gpt-5.4",
            SupportedReasoningEfforts = ["low", "high"]
        });

        Assert.True(metadata.SupportsReasoning);
    }
}

/// <summary>Pinning is an app-wide preference that has to survive a restart.</summary>
public class ChatViewModelModelPinningTests
{
    private static ChatViewModel NewSurface(AppData data) => new(new DataStore(data), TestCopilot.Shared);

    private static AppData NewData() => new() { Settings = new UserSettings { PreferredModel = "gpt-5.4" } };

    private static List<ModelInfo> Catalog() =>
    [
        new()
        {
            Id = "gpt-5.4",
            Capabilities = new ModelCapabilities
            {
                Limits = new ModelLimits { MaxContextWindowTokens = 128_000 },
                Supports = new ModelSupports { Vision = true, ReasoningEffort = true }
            },
            Billing = new ModelBilling { Multiplier = 1 }
        },
        new() { Id = "claude-opus-5" },
        new() { Id = "auto" }
    ];

    [Fact]
    public void ModelOptions_CarryCatalogMetadataForTheirBadges()
    {
        var surface = NewSurface(NewData());
        surface.UpdateModelCapabilities(Catalog());
        surface.ApplyAvailableModels(["gpt-5.4", "claude-opus-5", "auto"], "gpt-5.4");

        var option = surface.ModelOptions.Single(m => m.Id == "gpt-5.4");
        Assert.Equal(128_000, option.ContextTokens);
        Assert.Equal("128K context", option.ContextBadge);
        Assert.Equal("1\u00d7", option.CostBadge);
        Assert.True(option.SupportsVision);
        Assert.True(option.SupportsReasoning);
    }

    [Fact]
    public void ModelOptions_UseRawContextLimitWhenSdkMetadataOmitsIt()
    {
        var surface = NewSurface(NewData());
        surface.UpdateModelCapabilities(
            [new ModelInfo { Id = "gpt-5.4" }],
            new HashSet<string>(["gpt-5.4"], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase)
            {
                ["gpt-5.4"] = new(128_000, 1_000_000)
            });
        surface.ApplyAvailableModels(["gpt-5.4"], "gpt-5.4");

        var option = Assert.Single(surface.ModelOptions);
        Assert.Equal(1_000_000, option.ContextTokens);
        Assert.Equal("1M context", option.ContextBadge);
    }

    [Fact]
    public void TogglePinnedModel_MovesTheModelToTheTopAndPersists()
    {
        var data = NewData();
        var surface = NewSurface(data);
        surface.UpdateModelCapabilities(Catalog());
        surface.ApplyAvailableModels(["gpt-5.4", "claude-opus-5", "auto"], "gpt-5.4");

        var target = surface.ModelOptions.Single(m => m.Id == "claude-opus-5");
        surface.TogglePinnedModelCommand.Execute(target);

        Assert.Equal("claude-opus-5", surface.ModelOptions[0].Id);
        Assert.True(surface.ModelOptions[0].IsPinned);
        Assert.Equal(["claude-opus-5"], data.Settings.FavoriteModelIds);

        // A fresh surface over the same store is what a restart looks like.
        var restarted = NewSurface(data);
        restarted.UpdateModelCapabilities(Catalog());
        restarted.ApplyAvailableModels(["gpt-5.4", "claude-opus-5", "auto"], "gpt-5.4");

        Assert.Equal("claude-opus-5", restarted.ModelOptions[0].Id);
        Assert.True(restarted.ModelOptions[0].IsPinned);
    }

    [Fact]
    public void TogglePinnedModel_Unpins_AndRestoresCatalogOrder()
    {
        var data = NewData();
        var surface = NewSurface(data);
        surface.UpdateModelCapabilities(Catalog());
        surface.ApplyAvailableModels(["gpt-5.4", "claude-opus-5", "auto"], "gpt-5.4");

        var target = surface.ModelOptions.Single(m => m.Id == "auto");
        surface.TogglePinnedModelCommand.Execute(target);
        surface.TogglePinnedModelCommand.Execute(target);

        Assert.Empty(data.Settings.FavoriteModelIds);
        Assert.Equal(["gpt-5.4", "claude-opus-5", "auto"], surface.ModelOptions.Select(m => m.Id));
        Assert.All(surface.ModelOptions, option => Assert.False(option.IsPinned));
    }

    // Selection is referenced at 30+ call sites as a model-id string; rich rows must not change that.
    [Fact]
    public void SelectedModel_StaysAPlainModelId()
    {
        var surface = NewSurface(NewData());
        surface.UpdateModelCapabilities(Catalog());
        surface.ApplyAvailableModels(["gpt-5.4", "claude-opus-5"], "gpt-5.4");

        surface.SelectedModel = "claude-opus-5";

        Assert.Equal("claude-opus-5", surface.SelectedModel);
        Assert.Contains(surface.ModelOptions, option => option.Id == "claude-opus-5");
    }

    // The saved index is built by hand-copying every settings field, so a new field that is not
    // copied there is silently dropped on write — pins looked applied until the app restarted.
    [Fact]
    public void FavoriteModelIds_SurviveTheSavedIndexSnapshot()
    {
        var data = NewData();
        data.Settings.FavoriteModelIds = ["claude-opus-5", "gpt-5.4"];

        var snapshot = AppDataSnapshotFactory.CreateIndexSnapshot(data);

        Assert.Equal(["claude-opus-5", "gpt-5.4"], snapshot.Settings.FavoriteModelIds);
    }
}

/// <summary>
/// Control-level guards for rich model items: the picker must resolve them to their id when writing
/// the selection back, list pinned items first, and route the pin affordance to the host command.
/// </summary>
[Collection("Headless UI")]
public sealed class StrataModelPickerRichItemTests
{
    [Fact]
    public async Task ChoosingARichRow_WritesThePlainModelIdToSelectedModel()
    {
        using var session = HeadlessTestSession.Start();

        object? selected = null;

        await session.Dispatch(async () =>
        {
            var picker = new StrataModelPicker
            {
                Models = new ObservableCollection<ModelOption>
                {
                    new("gpt-5.4"),
                    new("claude-opus-5")
                },
                SelectedModel = "gpt-5.4"
            };

            var window = new Window { Width = 460, Height = 420, Content = picker };
            window.Show();
            await PumpAsync();

            ClickPickerButton(window, picker);
            await PumpAsync();

            ClickRow(picker, "claude-opus-5");
            await PumpAsync();

            selected = picker.SelectedModel;
            window.Close();
        }, CancellationToken.None);

        Assert.Equal("claude-opus-5", selected);
    }

    [Fact]
    public async Task PinnedRichItems_AreListedFirstUnderAPinnedHeader()
    {
        using var session = HeadlessTestSession.Start();

        var headers = Array.Empty<string>();
        var firstRowId = string.Empty;

        await session.Dispatch(async () =>
        {
            var picker = new StrataModelPicker
            {
                Models = new ObservableCollection<ModelOption>
                {
                    new("claude-opus-5") { IsPinned = true },
                    new("gpt-5.4")
                },
                SelectedModel = "gpt-5.4"
            };

            var window = new Window { Width = 460, Height = 420, Content = picker };
            window.Show();
            await PumpAsync();

            ClickPickerButton(window, picker);
            await PumpAsync();

            headers = ReadGroupHeaders(picker);
            firstRowId = ReadRowIds(picker).FirstOrDefault() ?? string.Empty;

            window.Close();
        }, CancellationToken.None);

        Assert.Equal("PINNED", headers.FirstOrDefault());
        Assert.Equal("claude-opus-5", firstRowId);
    }

    [Fact]
    public async Task PinStateChange_RebuildsOpenPickerWithoutACollectionMove()
    {
        using var session = HeadlessTestSession.Start();

        var headersBefore = Array.Empty<string>();
        var headersAfter = Array.Empty<string>();
        var pinVisualUpdated = false;

        await session.Dispatch(async () =>
        {
            // Pinning the first catalog item leaves it at index 0. The collection emits no Move, so
            // the picker has to observe IsPinned itself or its open row stays visually stale.
            var first = new ModelOption("gpt-5.4");
            var picker = new StrataModelPicker
            {
                Models = new ObservableCollection<ModelOption>
                {
                    first,
                    new("claude-opus-5")
                },
                SelectedModel = "gpt-5.4",
                ModelPinCommand = new DelegateCommand(_ => { })
            };

            var window = new Window { Width = 460, Height = 420, Content = picker };
            window.Show();
            await PumpAsync();

            ClickPickerButton(window, picker);
            await PumpAsync();
            headersBefore = ReadGroupHeaders(picker);

            first.IsPinned = true;
            await PumpAsync();

            headersAfter = ReadGroupHeaders(picker);
            pinVisualUpdated = FindPinButton(picker, first.Id).Classes.Contains("pinned");
            window.Close();
        }, CancellationToken.None);

        Assert.DoesNotContain("PINNED", headersBefore);
        Assert.Equal("PINNED", headersAfter.FirstOrDefault());
        Assert.True(pinVisualUpdated);
    }

    [Theory]
    [InlineData(false, "Pin to top")]
    [InlineData(true, "Unpin")]
    public async Task PinButton_HasAccessibleActionAndModelName(bool isPinned, string action)
    {
        using var session = HeadlessTestSession.Start();

        var accessibleName = string.Empty;

        await session.Dispatch(async () =>
        {
            var option = new ModelOption("gpt-5.4") { IsPinned = isPinned };
            var picker = new StrataModelPicker
            {
                Models = new ObservableCollection<ModelOption> { option },
                SelectedModel = option.Id,
                ModelPinCommand = new DelegateCommand(_ => { }),
                PinToolTip = "Pin to top",
                UnpinToolTip = "Unpin"
            };

            var window = new Window { Width = 460, Height = 420, Content = picker };
            window.Show();
            await PumpAsync();

            ClickPickerButton(window, picker);
            await PumpAsync();

            accessibleName = AutomationProperties.GetName(FindPinButton(picker, option.Id)) ?? string.Empty;
            window.Close();
        }, CancellationToken.None);

        Assert.Equal($"{action}: gpt-5.4", accessibleName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinButton_MouseClickRunsCommandWithoutSelectingTheRow(bool isPinned)
    {
        using var session = HeadlessTestSession.Start();

        object? pinned = null;
        object? selected = null;

        await session.Dispatch(async () =>
        {
            var option = new ModelOption("claude-opus-5") { IsPinned = isPinned };
            var picker = new StrataModelPicker
            {
                SelectedModel = "gpt-5.4",
                ModelPinCommand = new DelegateCommand(model => pinned = model)
            };

            // Host the real generated row directly instead of inside Popup. Avalonia Headless's
            // LightDismissOverlayLayer sits above popup content during coordinate hit-testing, but
            // the row itself is the exact production control tree and can be mouse-clicked normally.
            var createRow = typeof(StrataModelPicker).GetMethod(
                "CreateModelRow",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("CreateModelRow was not found.");
            var row = (Border)(createRow.Invoke(picker, [option, option.Id, false])
                ?? throw new InvalidOperationException("CreateModelRow returned null."));

            var window = new Window { Width = 360, Height = 100, Content = row };
            window.Show();
            await PumpAsync();

            var pin = row.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Classes.Contains("model-pin"));
            ClickControl(window, pin);
            await PumpAsync();

            selected = picker.SelectedModel;

            window.Close();
        }, CancellationToken.None);

        Assert.Equal("claude-opus-5", Assert.IsType<ModelOption>(pinned).Id);
        Assert.Equal("gpt-5.4", selected);
    }

    [Fact]
    public async Task EffortSegments_FollowTheSelectedQuality()
    {
        using var session = HeadlessTestSession.Start();

        var activeBefore = string.Empty;
        var readoutBefore = string.Empty;
        var activeAfter = string.Empty;
        var readoutAfter = string.Empty;
        var thumbVisible = false;

        await session.Dispatch(async () =>
        {
            var picker = new StrataModelPicker
            {
                Models = new ObservableCollection<ModelOption> { new("gpt-5.4") },
                SelectedModel = "gpt-5.4",
                QualityLevels = new[] { "Low", "Medium", "High" },
                SelectedQuality = "Low"
            };

            var window = new Window { Width = 460, Height = 460, Content = picker };
            window.Show();
            await PumpAsync();

            ClickPickerButton(window, picker);
            await PumpAsync();

            activeBefore = ReadActiveSegment(picker);
            readoutBefore = ReadSegmentReadout(picker);

            // Exactly what a host does when the effort is changed from outside the popup.
            picker.SelectedQuality = "High";
            await PumpAsync();

            activeAfter = ReadActiveSegment(picker);
            readoutAfter = ReadSegmentReadout(picker);
            thumbVisible = FindThumbs(picker).Any(thumb => thumb.IsVisible);

            window.Close();
        }, CancellationToken.None);

        Assert.Equal("Low", activeBefore);
        Assert.Equal("Low", readoutBefore);
        Assert.Equal("High", activeAfter);
        Assert.Equal("High", readoutAfter);
        Assert.True(thumbVisible, "the selection thumb should be shown once a segment is active");
    }

    // The section used to be rebuilt/cleared based on its child count. It is now tracked explicitly,
    // so dropping the levels must still tear the section down rather than leave a stale control.
    [Fact]
    public async Task EffortSection_IsRemovedWhenTheModelOffersNoLevels()
    {
        using var session = HeadlessTestSession.Start();

        var segmentsBefore = 0;
        var segmentsAfter = 0;

        await session.Dispatch(async () =>
        {
            var picker = new StrataModelPicker
            {
                Models = new ObservableCollection<ModelOption> { new("gpt-5.4") },
                SelectedModel = "gpt-5.4",
                QualityLevels = new[] { "Low", "High" },
                SelectedQuality = "Low"
            };

            var window = new Window { Width = 460, Height = 460, Content = picker };
            window.Show();
            await PumpAsync();

            ClickPickerButton(window, picker);
            await PumpAsync();
            segmentsBefore = FindSegments(picker).Count;

            picker.QualityLevels = null;
            await PumpAsync();
            segmentsAfter = FindSegments(picker).Count;

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(2, segmentsBefore);
        Assert.Equal(0, segmentsAfter);
    }

    private static List<Button> FindSegments(StrataModelPicker picker)
    {
        var panel = FindPart<Border>(picker, "PART_ModelPickerPanel");
        return panel is null
            ? []
            : panel.GetSelfAndVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("effort-seg"))
                .ToList();
    }

    private static List<Border> FindThumbs(StrataModelPicker picker)
    {
        var panel = FindPart<Border>(picker, "PART_ModelPickerPanel");
        return panel is null
            ? []
            : panel.GetSelfAndVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("segment-thumb"))
                .ToList();
    }

    private static string ReadActiveSegment(StrataModelPicker picker)
        => FindSegments(picker)
            .FirstOrDefault(button => button.Classes.Contains("active"))?
            .Content?.ToString() ?? string.Empty;

    private static string ReadSegmentReadout(StrataModelPicker picker)
    {
        var panel = FindPart<Border>(picker, "PART_ModelPickerPanel");
        return panel is null
            ? string.Empty
            : panel.GetSelfAndVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(text => text.Classes.Contains("segment-readout"))?
                .Text ?? string.Empty;
    }

    private static string[] ReadGroupHeaders(StrataModelPicker picker)
    {
        var list = FindPart<StackPanel>(picker, "PART_ModelPickerList");
        return list is null
            ? []
            : list.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(text => text.Classes.Contains("model-picker-group-header"))
                .Select(text => text.Text ?? string.Empty)
                .ToArray();
    }

    private static string[] ReadRowIds(StrataModelPicker picker) =>
        FindRows(picker).Select(row => row.Tag as string ?? string.Empty).ToArray();

    private static List<Border> FindRows(StrataModelPicker picker)
    {
        var list = FindPart<StackPanel>(picker, "PART_ModelPickerList");
        return list is null
            ? []
            : list.GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("model-picker-row"))
                .ToList();
    }

    private static Border FindRow(StrataModelPicker picker, string modelId)
        => FindRows(picker).FirstOrDefault(border => (border.Tag as string) == modelId)
           ?? throw new InvalidOperationException($"No picker row for '{modelId}'.");

    private static void ClickRow(StrataModelPicker picker, string modelId)
    {
        var selectionButton = FindRow(picker, modelId)
            .GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Classes.Contains("model-row-select"));
        selectionButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    private static Button FindPinButton(StrataModelPicker picker, string modelId)
    {
        var row = FindRows(picker).FirstOrDefault(border => (border.Tag as string) == modelId)
            ?? throw new InvalidOperationException($"No picker row for '{modelId}'.");

        return row.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("model-pin"))
            ?? throw new InvalidOperationException($"Row '{modelId}' has no pin button.");
    }

    private static void ClickControl(Window window, Control target)
    {
        var topLeft = target.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("Click target is not attached to the test window.");
        var point = topLeft + new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);

        window.MouseMove(point, RawInputModifiers.None);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static void ClickPickerButton(Window window, StrataModelPicker picker)
    {
        var button = FindPart<Button>(picker, "PART_ModelPickerButton")
            ?? throw new InvalidOperationException("PART_ModelPickerButton was not found.");

        var topLeft = button.TranslatePoint(new Point(0, 0), window)
            ?? throw new InvalidOperationException("PART_ModelPickerButton is not attached to the test window.");
        var point = topLeft + new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);

        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static T? FindPart<T>(StrataModelPicker picker, string name) where T : Control
    {
        var candidates = picker.GetVisualDescendants()
            .OfType<T>()
            .Concat(picker.GetVisualDescendants()
                .OfType<Popup>()
                .Where(popup => popup.IsOpen)
                .SelectMany(popup => popup.Child?.GetSelfAndVisualDescendants() ?? [])
                .OfType<T>())
            .ToList();

        return candidates.FirstOrDefault(control => control.Name == name)
            ?? candidates.FirstOrDefault();
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private sealed class DelegateCommand(Action<object?> execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }
}
