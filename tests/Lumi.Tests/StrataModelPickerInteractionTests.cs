using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme;
using StrataTheme.Controls;
using Xunit;
using ICommand = System.Windows.Input.ICommand;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class StrataModelPickerInteractionTests
{
    [Fact]
    public async Task FusionLeadsItsProviderWithoutChangingPinOrVersionOrder()
    {
        await WithOpenPickerAsync(() =>
        {
            var fusion = new ModelOption("auto:balance") { DisplayName = "Copilot Fusion" };
            fusion.UpdateSelectionAliases(["auto", "auto:balance", "auto:hydrafusion", "auto:hydrafusion-max"]);
            return new StrataModelPicker
            {
                Models = new[]
                {
                    new ModelOption("claude-opus-5.5") { IsPinned = true },
                    new ModelOption("gpt-6-luna") { IsPinned = true },
                    new ModelOption("gpt-5.5"),
                    fusion,
                    new ModelOption("gpt-6.1-sol"),
                    new ModelOption("byok:external") { ProviderName = "Connected account" }
                },
                SelectedModel = "auto:hydrafusion-max"
            };
        }, (picker, _) =>
        {
            var sections = StrataModelPicker.GetModelSections(picker.Models);
            Assert.True(sections[0].IsPinned);
            Assert.Equal(["claude-opus-5.5", "gpt-6-luna"],
                sections[0].Models.Cast<ModelOption>().Select(model => model.Id));
            Assert.Equal(["auto:balance", "gpt-6.1-sol", "gpt-5.5"],
                sections[1].Models.Cast<ModelOption>().Select(model => model.Id));
            Assert.Equal(["byok:external"], sections[2].Models.Cast<ModelOption>().Select(model => model.Id));
            Assert.Equal(["claude-opus-5.5", "gpt-6-luna", "auto:balance", "gpt-6.1-sol", "gpt-5.5", "byok:external"],
                ReadRows(picker).Select(row => row.Tag as string));
            Assert.Equal("auto:hydrafusion-max", picker.SelectedModel);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task GroupedModelRetainsItsVariantWhenHighlightedOrChosenAgain()
    {
        await WithOpenPickerAsync(() =>
        {
            var fusion = new ModelOption("auto:balance") { DisplayName = "Copilot Fusion" };
            fusion.UpdateSelectionAliases(["auto", "auto:balance", "auto:hydrafusion", "auto:hydrafusion-max"]);
            return new StrataModelPicker
            {
                Models = new[] { fusion },
                SelectedModel = "auto:hydrafusion-max",
                QualityLevels = new[] { "Standard", "High", "Max" },
                SelectedQuality = "Max"
            };
        }, (picker, _) =>
        {
            Assert.Equal("Copilot Fusion", picker.SelectedModelDisplayName);
            var row = Assert.Single(ReadRows(picker));
            Assert.Contains(row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(),
                path => path.Classes.Contains("model-picker-check") && path.IsVisible);
            new ButtonAutomationPeer(row.GetVisualDescendants().OfType<Button>().First()).Invoke();
            Assert.Equal("auto:hydrafusion-max", picker.SelectedModel);
            Assert.Equal("Max", picker.SelectedQuality);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task PickerEntrance_UsesAShortNearFullSizeMotionWithoutChangingOtherPopups()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var picker = NewPicker();
            var window = new Window { Width = 600, Height = 720, Content = picker };
            window.Show();
            try
            {
                await PumpAsync();
                var popup = FindPart<Popup>(picker, "PART_ModelPickerPopup");
                Assert.False(PopupAnimationHelper.GetEnableOpenAnimation(popup));
                Assert.True(PopupAnimationHelper.GetEnableOverlayAnimation(popup));
                Assert.Equal(TimeSpan.FromMilliseconds(120), PopupAnimationHelper.GetOpenAnimationDuration(popup));
                Assert.Equal(TimeSpan.FromMilliseconds(220), PopupAnimationHelper.GetOpenAnimationDuration(new Popup()));
                Assert.Equal(TimeSpan.FromMilliseconds(150), PopupAnimationHelper.CloseDuration);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ModernPicker_UsesOneSearchHeaderSingleLineRowsAndASettingsFooter()
    {
        await WithOpenPickerAsync(() => new StrataModelPicker
        {
            Models = Enumerable.Range(0, 64).Select(index => new ModelOption($"lab-model-{index:00}")).ToArray(),
            SelectedModel = "lab-model-63",
            ModelPinCommand = new DelegateCommand(_ => { }),
            QualityLevels = new[] { "Low", "Medium", "High" },
            SelectedQuality = "High",
            ContextWindowTiers = new[] { "Default", "Long" },
            SelectedContextWindowTier = "Default",
            ReasoningLabel = "Effort",
            ContextLabel = "Context"
        }, async (picker, _) =>
        {
            var panel = FindPart<Border>(picker, "PART_ModelPickerPanel");
            Assert.Equal(328, panel.Bounds.Width);
            Assert.InRange(panel.Bounds.Height, 300, 500);

            // Search, its icon and the pinned filter share one borderless header row.
            var header = FindPart<Grid>(picker, "PART_ModelPickerHeader");
            var search = FindPart<TextBox>(picker, "PART_ModelPickerSearch");
            var filter = FindPart<ToggleButton>(picker, "PART_ModelPickerPinnedFilter");
            Assert.True(filter.IsVisible);
            Assert.Same(header, search.GetVisualParent());
            Assert.Same(header, filter.GetVisualParent());
            Assert.Equal("Pinned", ToolTip.GetTip(filter));
            Assert.Equal(new Avalonia.Thickness(0), search.BorderThickness);
            Assert.DoesNotContain(search.GetVisualDescendants().OfType<Border>(), border => border.Name == "PART_Border");
            Assert.Throws<InvalidOperationException>(() => FindPart<TextBlock>(picker, "PART_ModelPickerCount"));

            // Rows are one line: the check marks the current model, and names share the heading edge.
            var rows = ReadRows(picker);
            var current = rows.Single(row => row.Tag as string == "lab-model-63");
            Assert.InRange(current.Bounds.Height, 30, 34);
            var check = current.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                .Single(path => path.Classes.Contains("model-picker-check"));
            Assert.True(check.IsVisible);
            Assert.All(rows.Where(row => row != current), row => Assert.DoesNotContain(
                row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(),
                path => path.Classes.Contains("model-picker-check") && path.IsVisible));
            var heading = FindPart<ListBox>(picker, "PART_ModelPickerList").GetVisualDescendants().OfType<TextBlock>()
                .First(text => text.Classes.Contains("model-picker-group-header"));
            var name = current.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "lab-model-63");
            var headingX = heading.TranslatePoint(default, panel)!.Value.X;
            var nameX = name.TranslatePoint(default, panel)!.Value.X;
            Assert.InRange(Math.Abs(headingX - nameX), 0, 1);

            // Reasoning and context sit in a footer with no text labels or icons: just the segmented
            // controls, spanning the same width as the search field's Strata line.
            var footer = FindPart<Border>(picker, "PART_ModelPickerFooter");
            Assert.True(footer.IsVisible);
            Assert.DoesNotContain(footer.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text is "Effort" or "Context");
            Assert.Empty(footer.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
            var tracks = footer.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("model-setting"))
                .OrderBy(border => border.TranslatePoint(default, panel)!.Value.Y).ToArray();
            Assert.Equal(2, tracks.Length);
            Assert.All(tracks, track => Assert.Contains("model-picker-segments", track.Classes));
            Assert.Equal(["Effort", "Context"], tracks.Select(track => ToolTip.GetTip(track) as string));
            Assert.Equal("Effort: High", AutomationProperties.GetName(tracks[0]));
            Assert.Equal("Context: Default", AutomationProperties.GetName(tracks[1]));
            Assert.All(ReadSegments(picker), segment => Assert.Contains(
                AutomationProperties.GetHelpText(segment), new[] { "Effort", "Context" }));

            var line = FindPart<Border>(picker, "PART_ModelPickerSearchLine");
            var lineLeft = header.TranslatePoint(new Point(line.Bounds.Left, 0), panel)!.Value.X;
            var lineRight = header.TranslatePoint(new Point(line.Bounds.Right, 0), panel)!.Value.X;
            foreach (var track in tracks)
            {
                Assert.InRange(Math.Abs(track.TranslatePoint(default, panel)!.Value.X - lineLeft), 0, 1);
                Assert.InRange(Math.Abs(track.TranslatePoint(new Point(track.Bounds.Width, 0), panel)!.Value.X - lineRight), 0, 1);

                // Segments fill their track, sharing the spare width.
                var segments = track.GetVisualDescendants().OfType<Button>().ToArray();
                Assert.Equal(track.Bounds.Width - track.Padding.Left - track.Padding.Right,
                    segments.Sum(segment => segment.Bounds.Width), 1);
            }

            var high = ReadSegments(picker).Single(button => button.Content?.ToString() == "High");
            var low = ReadSegments(picker).Single(button => button.Content?.ToString() == "Low");
            foreach (var segment in ReadSegments(picker))
                foreach (var text in segment.GetVisualDescendants().OfType<TextBlock>())
                    Assert.Equal(segment.FontSize, text.FontSize);
            // The thumb opens on the current choice (High) without animating in.
            var thumb = tracks[0].GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("segment-thumb"));
            Assert.Equal(high.Bounds.Width, thumb.Width, 1);
            new ButtonAutomationPeer(low).Invoke();
            await PumpAsync();
            Assert.Equal("Low", picker.SelectedQuality);
            Assert.Contains("active", low.Classes);
            Assert.DoesNotContain("active", high.Classes);
            Assert.Equal("Effort: Low", AutomationProperties.GetName(tracks[0]));

            var popup = FindPart<Popup>(picker, "PART_ModelPickerPopup");
            Assert.Equal(TimeSpan.FromMilliseconds(120), PopupAnimationHelper.GetOpenAnimationDuration(popup));
            Assert.Equal(TimeSpan.FromMilliseconds(220), PopupAnimationHelper.GetOpenAnimationDuration(new Popup()));
            Assert.True(popup.IsUsingOverlayLayer);

            picker.SearchText = "model 63";
            await PumpAsync();
            Assert.Equal(1, picker.FilteredModelCount);
            picker.ReasoningLabel = "Reasoning";
            await PumpAsync();
            Assert.Equal("Reasoning", ToolTip.GetTip(tracks[0]));
            Assert.Equal("Reasoning: Low", AutomationProperties.GetName(tracks[0]));
            Assert.Equal("Reasoning", AutomationProperties.GetHelpText(high));
        });
    }

    [Fact]
    public async Task SettingsFooter_IsHiddenWhenTheModelOffersNoChoices()
    {
        await WithOpenPickerAsync(NewPicker, async (picker, _) =>
        {
            var footer = FindPart<Border>(picker, "PART_ModelPickerFooter");
            Assert.False(footer.IsVisible);

            picker.QualityLevels = new[] { "Low", "High" };
            picker.SelectedQuality = "Low";
            await PumpAsync();
            Assert.True(footer.IsVisible);
            Assert.False(FindPart<StackPanel>(picker, "PART_ContextWindowSection").IsVisible);

            picker.QualityLevels = null;
            await PumpAsync();
            Assert.False(footer.IsVisible);
        });
    }

    [Fact]
    public async Task SettingsRow_KeepsEverySegmentOnOneLineInsideTheFooterWhenSpaceRunsOut()
    {
        await WithOpenPickerAsync(() =>
        {
            var picker = NewPicker();
            picker.ReasoningLabel = "Reasoning effort for this model";
            picker.QualityLevels = new[] { "Minimal", "Low", "Medium", "High", "Extra high", "Maximum effort" };
            picker.SelectedQuality = "Maximum effort";
            return picker;
        }, async (picker, window) =>
        {
            window.UpdateLayout();
            await PumpAsync();
            var footer = FindPart<Border>(picker, "PART_ModelPickerFooter");
            var segments = ReadSegments(picker);
            Assert.Equal(6, segments.Length);
            var rowTop = segments[0].TranslatePoint(default, footer)!.Value.Y;
            foreach (var segment in segments)
            {
                var topLeft = segment.TranslatePoint(default, footer)!.Value;
                Assert.True(topLeft.X >= 0, $"{segment.Content} starts outside the footer.");
                Assert.True(topLeft.X + segment.Bounds.Width <= footer.Bounds.Width + 0.5, $"{segment.Content} ends outside the footer.");
                Assert.Equal(rowTop, topLeft.Y, 0.5);
            }

            var thumb = footer.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("segment-thumb"));
            var active = segments.Single(segment => segment.Classes.Contains("active"));
            Assert.Equal(active.Bounds.Width, thumb.Width, 1);
        });
    }

    [Fact]
    public async Task PickerSurface_UsesTheStrataLineAndInWindowBackgroundBlur()
    {
        await WithOpenPickerAsync(NewPicker, async (picker, window) =>
        {
            var search = FindPart<TextBox>(picker, "PART_ModelPickerSearch");
            var line = FindPart<Border>(picker, "PART_ModelPickerSearchLine");
            var panel = FindPart<Border>(picker, "PART_ModelPickerPanel");
            var popup = FindPart<Popup>(picker, "PART_ModelPickerPopup");

            line.Transitions = null;
            search.Focus();
            await PumpAsync();
            Assert.True(search.IsFocused);
            Assert.Equal(1, line.Opacity);
            Assert.Equal(2, line.Height);
            Assert.Equal(VerticalAlignment.Bottom, line.VerticalAlignment);
            Assert.IsAssignableFrom<IGradientBrush>(line.Background);
            Assert.Equal(new Avalonia.Thickness(0), search.BorderThickness);

            FindPart<Button>(picker, "PART_ModelPickerButton").Focus();
            await PumpAsync();
            Assert.Equal(0, line.Opacity);

            Assert.True(popup.ShouldUseOverlayLayer);
            Assert.True(popup.IsUsingOverlayLayer);
            Assert.Empty(panel.GetVisualDescendants().OfType<ExperimentalAcrylicBorder>());
            Assert.Same(window, TopLevel.GetTopLevel(panel));
            var backdrop = FindPart<Border>(picker, "PART_ModelPickerBackdrop");
            var frost = FindPart<StrataBackdropBlur>(picker, "PART_ModelPickerFrost");
            Assert.Equal(20, frost.BlurRadius);
            Assert.Equal(new CornerRadius(11), frost.CornerRadius);
            Assert.False(frost.IsHitTestVisible);
            Assert.Null(backdrop.Effect);
            Assert.Null(backdrop.Background);
            Assert.DoesNotContain(panel.GetSelfAndVisualDescendants().OfType<Border>(),
                border => border.Background is VisualBrush);
            Assert.Null(panel.Effect);
            Assert.Null(search.Effect);
            Assert.True(FindPart<Border>(picker, "PART_ModelPickerTint").IsVisible);

            search.Text = "opus";
            await PumpAsync();
            Assert.Equal(1, picker.FilteredModelCount);
        });
    }

    [Fact]
    public async Task PointerMove_MovesTheActiveRowWithoutChoosingIt()
    {
        await WithOpenPickerAsync(NewPicker, async (picker, _) =>
        {
            var list = FindPart<ListBox>(picker, "PART_ModelPickerList");
            Assert.Equal("gpt-5.4", ActiveRowId(list));
            var target = ReadRows(picker).Single(row => row.Tag as string == "byok:local");
            var text = target.GetVisualDescendants().OfType<TextBlock>().First();
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            text.RaiseEvent(new PointerEventArgs(
                InputElement.PointerMovedEvent, text, pointer, (Visual)TopLevel.GetTopLevel(text)!, default, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
            await PumpAsync();

            Assert.Equal("byok:local", ActiveRowId(list));
            Assert.Equal("gpt-5.4", picker.SelectedModel);
            PressKey(picker, Key.Enter);
            await PumpAsync();
            Assert.Equal("byok:local", picker.SelectedModel);
        });
    }

    [Fact]
    public async Task OpeningAndArrowKeys_KeepTheActiveRowInView()
    {
        var models = Enumerable.Range(0, 64).Select(index => new ModelOption($"lab-model-{index:00}")).ToArray();
        await WithOpenPickerAsync(() => new StrataModelPicker { Models = models, SelectedModel = "lab-model-10" },
            async (picker, window) =>
        {
            // Rows are ordered newest first, so the current model starts near the bottom of the list.
            var list = FindPart<ListBox>(picker, "PART_ModelPickerList");
            var viewer = (ScrollViewer)list.Scroll!;
            window.UpdateLayout();
            await PumpAsync();
            Assert.True(viewer.Offset.Y > 0);
            AssertActiveRowVisible(list, viewer, "lab-model-10");

            for (var step = 0; step < 60; step++)
                PressKey(picker, Key.Up);
            window.UpdateLayout();
            await PumpAsync();
            Assert.Equal("lab-model-63", ActiveRowId(list));
            Assert.Equal(0, viewer.Offset.Y);

            for (var step = 0; step < 40; step++)
                PressKey(picker, Key.Down);
            window.UpdateLayout();
            await PumpAsync();
            AssertActiveRowVisible(list, viewer, "lab-model-23");
            Assert.Equal("lab-model-10", picker.SelectedModel);
        });
    }

    private static string? ActiveRowId(ListBox list)
        => list.ContainerFromIndex(list.SelectedIndex) is ListBoxItem container
            ? StrataModelPicker.GetRowContent(container)?.Tag as string
            : null;

    [Fact]
    public async Task PinningTheActiveRow_KeepsTheScrollPosition()
    {
        var models = Enumerable.Range(0, 64).Select(index => new ModelOption($"lab-model-{index:00}")).ToArray();
        await WithOpenPickerAsync(
            () => new StrataModelPicker
            {
                Models = models,
                SelectedModel = "lab-model-10",
                ModelPinCommand = new DelegateCommand(_ => { })
            },
            async (picker, window) =>
        {
            var list = FindPart<ListBox>(picker, "PART_ModelPickerList");
            var viewer = (ScrollViewer)list.Scroll!;
            window.UpdateLayout();
            await PumpAsync();
            Assert.True(viewer.Offset.Y > 0);
            Assert.Equal("lab-model-10", ActiveRowId(list));

            // The active row moves into a new Pinned group at the top; the view must not chase it.
            models[10].IsPinned = true;
            await PumpAsync();
            window.UpdateLayout();
            await PumpAsync();

            Assert.True(viewer.Offset.Y > 0, "Pinning must not jump the list back to the top.");
            Assert.Equal("lab-model-10", picker.SelectedModel);
        });
    }

    private static void AssertActiveRowVisible(ListBox list, ScrollViewer viewer, string? expectedId)
    {
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(list.SelectedIndex));
        if (expectedId is not null)
            Assert.Equal(expectedId, StrataModelPicker.GetRowContent(container)?.Tag);
        var top = container.TranslatePoint(default, viewer)!.Value.Y;
        Assert.InRange(top, -0.5, viewer.Viewport.Height - container.Bounds.Height + 0.5);
    }

    [Fact]
    public async Task ClosingPicker_DetachesItsFrostWithoutRetainingAWindowBrush()
    {
        await WithOpenPickerAsync(NewPicker, async (picker, window) =>
        {
            var backdrop = FindPart<Border>(picker, "PART_ModelPickerBackdrop");
            var frost = FindPart<StrataBackdropBlur>(picker, "PART_ModelPickerFrost");
            Assert.True(frost.IsAttachedToVisualTree());
            Assert.Null(backdrop.Background);
            var detached = new TaskCompletionSource<bool>();
            backdrop.DetachedFromVisualTree += (_, _) => detached.TrySetResult(true);
            FindPart<Popup>(picker, "PART_ModelPickerPopup").IsOpen = false;
            await detached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await PumpAsync();

            Assert.Null(backdrop.Background);
            Assert.False(frost.IsAttachedToVisualTree());
            Assert.Equal("gpt-5.4", picker.SelectedModel);
        });
    }

    [Theory]
    [InlineData("GPT 5.4", "gpt-5.4")]
    [InlineData("  Opus CLAUDE  ", "claude-opus-5")]
    [InlineData("OpenAI", "gpt-5.4")]
    [InlineData("Anthropic", "claude-opus-5")]
    [InlineData("swift Personal", "byok:local")]
    [InlineData("local-model", "byok:local")]
    public async Task Search_MatchesNamesIdsProvidersAndEveryWord(string query, string expectedId)
    {
        await WithOpenPickerAsync(NewPicker, async (picker, _) =>
        {
            FindPart<TextBox>(picker, "PART_ModelPickerSearch").Text = query;
            await PumpAsync();

            Assert.Equal(query, picker.SearchText);
            Assert.Equal(1, picker.FilteredModelCount);
            Assert.Equal([expectedId], ReadRowIds(picker));
            Assert.Equal("gpt-5.4", picker.SelectedModel);
        });
    }

    [Fact]
    public async Task EmptySearch_DoesNotChangeSelectionOrSelectOnEnter()
    {
        await WithOpenPickerAsync(NewPicker, async (picker, _) =>
        {
            picker.SearchText = "no-such-model";
            await PumpAsync();
            PressKey(picker, Key.Enter);
            await PumpAsync();

            Assert.Equal(0, picker.FilteredModelCount);
            Assert.True(FindPart<TextBlock>(picker, "PART_ModelPickerNoResults").IsVisible);
            Assert.Equal("gpt-5.4", picker.SelectedModel);

            picker.SearchText = string.Empty;
            await PumpAsync();
            Assert.Equal(3, picker.FilteredModelCount);
            Assert.False(FindPart<TextBlock>(picker, "PART_ModelPickerNoResults").IsVisible);
        });
    }

    [Fact]
    public async Task Filtering_ResizesTheFrostWithoutCreatingANativePopup()
    {
        await WithOpenPickerAsync(() => new StrataModelPicker
        {
            Models = Enumerable.Range(0, 64).Select(index => $"gpt-5.4-lab-{index:00}").ToArray(),
            SelectedModel = "gpt-5.4-lab-00"
        }, async (picker, window) =>
        {
            var backdrop = FindPart<Border>(picker, "PART_ModelPickerBackdrop");
            var frost = FindPart<StrataBackdropBlur>(picker, "PART_ModelPickerFrost");
            var initialSize = frost.Bounds.Size;
            picker.SearchText = "lab 63";
            await PumpAsync();
            window.UpdateLayout();
            await PumpAsync();
            Assert.Null(backdrop.Background);
            Assert.NotEqual(initialSize, frost.Bounds.Size);
            Assert.Equal(backdrop.Bounds.Size, frost.Bounds.Size);
            Assert.True(FindPart<Popup>(picker, "PART_ModelPickerPopup").IsUsingOverlayLayer);
            Assert.Equal(1, picker.FilteredModelCount);

            picker.SearchText = "no-such-model";
            await PumpAsync();
            Assert.Same(window, TopLevel.GetTopLevel(backdrop));
            Assert.Equal(0, picker.FilteredModelCount);
        });
    }

    [Fact]
    public async Task Search_UpdatesWhenModelsAreAddedRemovedOrRenamed()
    {
        var models = new ObservableCollection<ModelOption> { new("gpt-5.4") };
        await WithOpenPickerAsync(
            () => new StrataModelPicker { Models = models, SelectedModel = "gpt-5.4" },
            async (picker, _) =>
        {
            picker.SearchText = "swift personal";
            await PumpAsync();
            Assert.Equal(0, picker.FilteredModelCount);

            var option = new ModelOption("byok:new") { DisplayName = "Swift", ProviderName = "Personal" };
            models.Add(option);
            await PumpAsync();
            Assert.Equal(["byok:new"], ReadRowIds(picker));

            option.DisplayName = "Other";
            await PumpAsync();
            Assert.Equal(0, picker.FilteredModelCount);

            option.DisplayName = "Swift";
            await PumpAsync();
            Assert.Equal(1, picker.FilteredModelCount);
            models.Remove(option);
            await PumpAsync();
            Assert.Equal(0, picker.FilteredModelCount);
            Assert.Equal("gpt-5.4", picker.SelectedModel);
        });
    }

    [Fact]
    public async Task PinnedFilter_ObservesPinChangesWithoutChangingSelection()
    {
        await WithOpenPickerAsync(NewPicker, async (picker, _) =>
        {
            var models = (ObservableCollection<ModelOption>)picker.Models!;
            models[1].IsPinned = true;
            picker.ModelPinCommand = new DelegateCommand(_ => { });
            picker.ShowPinnedOnly = true;
            await PumpAsync();
            Assert.Equal(["claude-opus-5"], ReadRowIds(picker));
            Assert.Equal("gpt-5.4", picker.SelectedModel);

            models[0].IsPinned = true;
            models[1].IsPinned = false;
            await PumpAsync();
            Assert.Equal(["gpt-5.4"], ReadRowIds(picker));

            picker.ShowPinnedOnly = false;
            await PumpAsync();
            Assert.Equal(3, picker.FilteredModelCount);
        });
    }

    [Fact]
    public async Task KeyboardNavigation_SkipsHeadersAndCommitsOnlyOnEnter()
    {
        await WithOpenPickerAsync(NewPicker, async (picker, _) =>
        {
            var search = FindPart<TextBox>(picker, "PART_ModelPickerSearch");
            Assert.True(search.IsFocused);
            PressKey(picker, Key.Down);
            await PumpAsync();
            Assert.Equal("gpt-5.4", picker.SelectedModel);

            PressKey(picker, Key.Enter);
            await PumpAsync();
            Assert.Equal("claude-opus-5", picker.SelectedModel);
            Assert.True(FindPart<Popup>(picker, "PART_ModelPickerPopup").IsOpen);

            PressKey(picker, Key.Down);
            PressKey(picker, Key.Enter);
            await PumpAsync();
            Assert.Equal("byok:local", picker.SelectedModel);

            PressKey(picker, Key.Up);
            PressKey(picker, Key.Up);
            PressKey(picker, Key.Enter);
            await PumpAsync();
            Assert.Equal("gpt-5.4", picker.SelectedModel);

            PressKey(picker, Key.Escape);
            await PumpAsync();
            Assert.False(FindPart<Popup>(picker, "PART_ModelPickerPopup").IsOpen);
            Assert.True(FindPart<Button>(picker, "PART_ModelPickerButton").IsFocused);
        });
    }

    [Fact]
    public async Task Reopening_ChecksAModelChosenWhileThePickerWasClosed()
    {
        await WithOpenPickerAsync(NewPicker, async (picker, _) =>
        {
            Assert.Equal(["gpt-5.4"], ReadCheckedRowIds(picker));
            FindPart<Popup>(picker, "PART_ModelPickerPopup").IsOpen = false;
            await PumpAsync();

            // For example, switching to a chat that uses another model while the picker is closed.
            picker.SelectedModel = "claude-opus-5";
            OpenPicker(picker);
            await PumpAsync();

            Assert.Equal(["claude-opus-5"], ReadCheckedRowIds(picker));
        });
    }

    [Fact]
    public async Task CommandP_PinsTheHighlightedRowWithoutChoosingIt()
    {
        object? pinned = null;
        await WithOpenPickerAsync(() =>
        {
            var picker = NewPicker();
            picker.ModelPinCommand = new DelegateCommand(model => pinned = model);
            picker.PinToolTip = "Pin to top";
            return picker;
        }, async (picker, window) =>
        {
            var modifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            PressKey(picker, Key.Down);
            PressKey(picker, Key.P, modifiers);
            await PumpAsync();

            Assert.Equal("claude-opus-5", Assert.IsType<ModelOption>(pinned).Id);
            Assert.Equal("gpt-5.4", picker.SelectedModel);
            Assert.True(FindPart<Popup>(picker, "PART_ModelPickerPopup").IsOpen);

            // The shortcut is discoverable from the pin button and exposed to assistive technology.
            var shortcut = new KeyGesture(Key.P, modifiers).ToString("p", null);
            var pin = ReadRows(picker).Single(row => row.Tag as string == "claude-opus-5")
                .GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("model-pin"));
            Assert.Equal($"Pin to top ({shortcut})", ToolTip.GetTip(pin));
            Assert.Equal(shortcut, AutomationProperties.GetAcceleratorKey(pin));
        });
    }

    [Fact]
    public async Task EnterOnTheFocusedPinnedFilter_TogglesItInsteadOfChoosingAModel()
    {
        await WithOpenPickerAsync(() =>
        {
            var picker = NewPicker();
            picker.ModelPinCommand = new DelegateCommand(_ => { });
            return picker;
        }, async (picker, _) =>
        {
            PressKey(picker, Key.Down);
            var filter = FindPart<ToggleButton>(picker, "PART_ModelPickerPinnedFilter");
            filter.Focus();
            filter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await PumpAsync();

            Assert.Equal("gpt-5.4", picker.SelectedModel);
            Assert.True(picker.ShowPinnedOnly);
        });
    }

    [Fact]
    public async Task Opening_BuildsNonRecyclingRowContentOnlyOncePerRealizedRow()
    {
        var created = 0;
        await WithOpenPickerAsync(() => new StrataModelPicker
        {
            Models = Enumerable.Range(0, 64).Select(index => new ModelOption($"lab-model-{index:00}")).ToArray(),
            SelectedModel = "lab-model-63",
            SelectedModelTemplate = new FuncDataTemplate<string>((id, _) => new TextBlock { Text = id }),
            ModelItemTemplate = new FuncDataTemplate<ModelOption>((option, _) =>
            {
                created++;
                return new TextBlock { Text = option?.DisplayName };
            }, supportsRecycling: false)
        }, (picker, _) =>
        {
            var rows = ReadRows(picker);
            Assert.InRange(rows.Length, 1, 24);
            Assert.Equal(rows.Length, created);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task LargeCatalog_VirtualizesAndRecyclesRowsWithCorrectSelectionAndPinTargets()
    {
        var models = Enumerable.Range(0, 256).Select(index => new ModelOption($"gpt-5.4-lab-{index:000}")).ToArray();
        var createdContent = 0;
        object? pinTarget = null;
        await WithOpenPickerAsync(() => new StrataModelPicker
        {
            Models = models,
            SelectedModel = models[0].Id,
            ModelPinCommand = new DelegateCommand(option => pinTarget = option),
            SelectedModelTemplate = new FuncDataTemplate<string>((id, _) => new TextBlock { Text = id }),
            ModelItemTemplate = new FuncDataTemplate<ModelOption>((option, _) =>
            {
                createdContent++;
                var text = new TextBlock();
                text.Bind(TextBlock.TextProperty, new Binding(nameof(ModelOption.DisplayName)));
                return text;
            }, supportsRecycling: true)
        }, async (picker, window) =>
        {
            Assert.Equal(256, picker.FilteredModelCount);
            Assert.InRange(ReadRows(picker).Length, 1, 24);
            Assert.InRange(createdContent, 1, 40);

            var list = FindPart<ListBox>(picker, "PART_ModelPickerList");
            foreach (var container in list.GetVisualDescendants().OfType<ListBoxItem>())
                Assert.Same(StrataModelPicker.GetRowContent(container), container.Presenter?.Child);
            var uniqueRows = new HashSet<Control>(ReadRows(picker), ReferenceEqualityComparer.Instance);
            var uniqueContainers = new HashSet<ListBoxItem>(
                list.GetVisualDescendants().OfType<ListBoxItem>(), ReferenceEqualityComparer.Instance);
            var counts = new List<int> { createdContent };
            list.SelectedIndex = list.Items.Count - 1;
            list.ScrollIntoView(list.SelectedIndex);
            window.UpdateLayout();
            await PumpAsync();
            var lastRow = ReadRows(picker).Single(row => row.Tag as string == models[^1].Id);
            Assert.Contains(lastRow.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == models[^1].DisplayName);
            var pin = lastRow.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("model-pin"));
            new ButtonAutomationPeer(pin).Invoke();
            await PumpAsync();
            Assert.Same(models[^1], pinTarget);
            Assert.Equal(models[0].Id, picker.SelectedModel);

            var select = lastRow.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("model-row-select"));
            new ButtonAutomationPeer(select).Invoke();
            await PumpAsync();
            Assert.Equal(models[^1].Id, picker.SelectedModel);
            Assert.InRange(ReadRows(picker).Length, 1, 24);

            for (var step = 0; step <= 8; step++)
            {
                list.SelectedIndex = (list.Items.Count - 1) * step / 8;
                list.ScrollIntoView(list.SelectedIndex);
                window.UpdateLayout();
                await PumpAsync();
                uniqueRows.UnionWith(ReadRows(picker));
                uniqueContainers.UnionWith(list.GetVisualDescendants().OfType<ListBoxItem>());
                counts.Add(createdContent);
            }
            Assert.True(createdContent <= 40,
                $"Templates={createdContent}, rows={uniqueRows.Count}, containers={uniqueContainers.Count}, counts={string.Join(",", counts)}");
        });
    }

    [Fact]
    public async Task CatalogBatch_RefreshesTheItemSourceOnlyOnce()
    {
        await WithOpenPickerAsync(NewPicker, async (picker, _) =>
        {
            var models = (ObservableCollection<ModelOption>)picker.Models!;
            var sourceChanges = 0;
            var list = FindPart<ListBox>(picker, "PART_ModelPickerList");
            list.PropertyChanged += (_, change) =>
            {
                if (change.Property == ItemsControl.ItemsSourceProperty)
                    sourceChanges++;
            };
            for (var index = 0; index < 20; index++)
                models.Add(new ModelOption($"claude-sonnet-5-lab-{index}"));
            models[0].IsPinned = true;
            models[1].ProviderName = "Personal provider";
            await PumpAsync();

            Assert.Equal(23, picker.FilteredModelCount);
            Assert.Equal(1, sourceChanges);
        });
    }

    [Fact]
    public async Task MetadataChange_UpdatesBadgesWithoutReplacingTheItemSource()
    {
        var option = new ModelOption("gpt-5.4") { CostMultiplier = 1 };
        await WithOpenPickerAsync(() => new StrataModelPicker
        {
            Models = new ObservableCollection<ModelOption> { option },
            SelectedModel = option.Id,
            SelectedModelTemplate = new FuncDataTemplate<string>((id, _) => new TextBlock { Text = id }),
            ModelItemTemplate = new FuncDataTemplate<ModelOption>((_, _) =>
            {
                var text = new TextBlock();
                text.Bind(TextBlock.TextProperty, new Binding(nameof(ModelOption.CostBadge)));
                return text;
            }, supportsRecycling: true)
        }, async (picker, _) =>
        {
            var source = FindPart<ListBox>(picker, "PART_ModelPickerList").ItemsSource;
            option.CostMultiplier = 0;
            await PumpAsync();

            Assert.Same(source, FindPart<ListBox>(picker, "PART_ModelPickerList").ItemsSource);
            Assert.Contains(ReadRows(picker).Single().GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == option.CostBadge);
        });
    }

    [Fact]
    public async Task EmptyCatalog_CanOpenAndAcceptItsFirstModel()
    {
        var models = new ObservableCollection<ModelOption>();
        await WithOpenPickerAsync(() => new StrataModelPicker { Models = models }, async (picker, _) =>
        {
            Assert.Equal(0, picker.FilteredModelCount);
            Assert.Null(picker.SelectedModel);
            Assert.True(FindPart<TextBlock>(picker, "PART_ModelPickerNoResults").IsVisible);

            models.Add(new ModelOption("gpt-5.4"));
            await PumpAsync();
            Assert.Equal(1, picker.FilteredModelCount);
            Assert.Equal("gpt-5.4", picker.SelectedModel);
        });
    }

    [Fact]
    public async Task Reopening_RetainsUnchangedCatalogAndSegmentsAndResetsFilters()
    {
        await WithOpenPickerAsync(() =>
        {
            var picker = NewPicker();
            picker.QualityLevels = new[] { "Low", "High" };
            picker.SelectedQuality = "High";
            picker.ContextWindowTiers = new[] { "Default", "Long context" };
            picker.SelectedContextWindowTier = "Default";
            return picker;
        }, async (picker, _) =>
        {
            var list = FindPart<ListBox>(picker, "PART_ModelPickerList");
            var source = list.ItemsSource;
            var segments = ReadSegments(picker);
            var popup = FindPart<Popup>(picker, "PART_ModelPickerPopup");
            popup.IsOpen = false;
            await PumpAsync();
            OpenPicker(picker);
            await PumpAsync();

            Assert.Same(source, list.ItemsSource);
            Assert.Equal(segments.Length, ReadSegments(picker).Length);
            Assert.All(segments.Zip(ReadSegments(picker)), pair => Assert.Same(pair.First, pair.Second));

            // Typed the way a user types (SetCurrentValue), so the box stays bound to the picker.
            var search = FindPart<TextBox>(picker, "PART_ModelPickerSearch");
            search.SetCurrentValue(TextBox.TextProperty, "opus");
            picker.ShowPinnedOnly = true;
            await PumpAsync();
            Assert.Equal("opus", picker.SearchText);
            popup.IsOpen = false;
            await PumpAsync();
            OpenPicker(picker);
            await PumpAsync();
            Assert.Equal(string.Empty, picker.SearchText);
            Assert.True(string.IsNullOrEmpty(search.Text));
            Assert.False(picker.ShowPinnedOnly);
            Assert.Equal(3, picker.FilteredModelCount);
        });
    }

    [Fact]
    public async Task Reopening_RefreshesANonObservableModelList()
    {
        var models = new List<string> { "gpt-5.4" };
        await WithOpenPickerAsync(
            () => new StrataModelPicker { Models = models, SelectedModel = "gpt-5.4" },
            async (picker, _) =>
        {
            FindPart<Popup>(picker, "PART_ModelPickerPopup").IsOpen = false;
            await PumpAsync();
            models.Add("claude-opus-5");
            OpenPicker(picker);
            await PumpAsync();
            Assert.Equal(2, picker.FilteredModelCount);
            Assert.Contains("claude-opus-5", ReadRowIds(picker));
        });
    }

    [Fact]
    public async Task ReattachingPicker_RestoresInteractionsAndCatalogObservation()
    {
        await WithOpenPickerAsync(NewPicker, async (picker, window) =>
        {
            var models = (ObservableCollection<ModelOption>)picker.Models!;
            FindPart<Popup>(picker, "PART_ModelPickerPopup").IsOpen = false;
            await PumpAsync();
            window.Content = null;
            await PumpAsync();
            models.Add(new ModelOption("gemini-3.1-pro"));
            window.Content = picker;
            await PumpAsync();
            OpenPicker(picker);
            await PumpAsync();
            Assert.Equal(4, picker.FilteredModelCount);

            picker.SearchText = "gemini";
            await PumpAsync();
            PressKey(picker, Key.Enter);
            await PumpAsync();
            Assert.Equal("gemini-3.1-pro", picker.SelectedModel);
        });
    }

    [Fact]
    public void LocalizedLabelStyle_SetsEveryPickerLabelFromLoc()
    {
        // Built from Loc when called (after the saved language loads), not when App.axaml is parsed.
        var setters = Lumi.App.CreateModelPickerTextStyle().Setters.OfType<Avalonia.Styling.Setter>()
            .ToDictionary(setter => setter.Property!, setter => setter.Value);

        Assert.Equal(8, setters.Count);
        Assert.Equal(Lumi.Localization.Loc.ModelPicker_SearchWatermark, setters[StrataModelPicker.SearchWatermarkProperty]);
        Assert.Equal(Lumi.Localization.Loc.ModelPicker_NoResults, setters[StrataModelPicker.NoResultsTextProperty]);
        Assert.Equal(Lumi.Localization.Loc.ModelPicker_Models, setters[StrataModelPicker.ModelsLabelProperty]);
        Assert.Equal(Lumi.Localization.Loc.ModelPicker_Pinned, setters[StrataModelPicker.PinnedLabelProperty]);
        Assert.Equal(Lumi.Localization.Loc.Get("ModelPicker_Effort"), setters[StrataModelPicker.ReasoningLabelProperty]);
        Assert.Equal(Lumi.Localization.Loc.ModelPicker_Context, setters[StrataModelPicker.ContextLabelProperty]);
        Assert.Equal(Lumi.Localization.Loc.ModelPicker_Pin, setters[StrataModelPicker.PinToolTipProperty]);
        Assert.Equal(Lumi.Localization.Loc.ModelPicker_Unpin, setters[StrataModelPicker.UnpinToolTipProperty]);
    }

    private static StrataModelPicker NewPicker() => new()
    {
        Models = new ObservableCollection<ModelOption>
        {
            new("gpt-5.4"), new("claude-opus-5"),
            new("byok:local") { DisplayName = "Swift local model", ProviderName = "Personal" }
        },
        SelectedModel = "gpt-5.4"
    };

    private static async Task WithOpenPickerAsync(
        Func<StrataModelPicker> createPicker, Func<StrataModelPicker, Window, Task> assertions)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var picker = createPicker();
            picker.VerticalAlignment = VerticalAlignment.Bottom;
            var window = new Window { Width = 600, Height = 720, Content = picker };
            window.Show();
            try
            {
                await PumpAsync();
                var popup = FindPart<Popup>(picker, "PART_ModelPickerPopup");
                PopupAnimationHelper.SetEnableOpenAnimation(popup, false);
                PopupAnimationHelper.SetEnableOverlayAnimation(popup, false);
                OpenPicker(picker);
                await PumpAsync();
                await assertions(picker, window);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static void OpenPicker(StrataModelPicker picker)
        => new ButtonAutomationPeer(FindPart<Button>(picker, "PART_ModelPickerButton")).Invoke();

    private static void PressKey(StrataModelPicker picker, Key key, KeyModifiers modifiers = KeyModifiers.None)
        => FindPart<TextBox>(picker, "PART_ModelPickerSearch").RaiseEvent(
            new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    private static string[] ReadCheckedRowIds(StrataModelPicker picker)
        => ReadRows(picker)
            .Where(row => row.IsEffectivelyVisible && row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                .Any(path => path.Classes.Contains("model-picker-check") && path.IsVisible))
            .Select(row => row.Tag as string ?? string.Empty)
            .ToArray();

    private static Control[] ReadRows(StrataModelPicker picker)
        => FindPart<ListBox>(picker, "PART_ModelPickerList").GetVisualDescendants().OfType<Control>()
            .Where(row => row.Classes.Contains("model-picker-row")).ToArray();

    private static string[] ReadRowIds(StrataModelPicker picker)
        => ReadRows(picker).Select(row => row.Tag as string ?? string.Empty).ToArray();

    private static Button[] ReadSegments(StrataModelPicker picker)
        => FindPart<Popup>(picker, "PART_ModelPickerPopup").Child!.GetSelfAndVisualDescendants()
            .OfType<Button>().Where(button => button.Classes.Contains("effort-seg")).ToArray();

    private static T FindPart<T>(StrataModelPicker picker, string name) where T : Control
        => picker.GetVisualDescendants().OfType<T>()
            .Concat(picker.GetVisualDescendants().OfType<Popup>()
                .SelectMany(popup => popup.Child?.GetSelfAndVisualDescendants() ?? [])
                .OfType<T>())
            .FirstOrDefault(control => control.Name == name)
            ?? throw new InvalidOperationException($"Picker part '{name}' was not found.");

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }
}
