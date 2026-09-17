using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Regression guard for transcript collapsible controls (StrataThink, StrataAiToolCall, …).
/// The transcript now hosts each item's view directly (TranscriptTurnControl builds the
/// DataTemplate and assigns <c>DataContext = item</c> instead of wrapping it in a
/// ContentPresenter). That hosting churns the built view's DataContext (set on realize,
/// cleared on release/reconcile). With a OneWay <c>IsExpanded</c> binding, every DataContext
/// re-assignment re-pushes the view-model value and clobbers the user's manual collapse, so
/// expanded items could no longer be collapsed. <c>IsExpanded</c> must therefore bind TwoWay
/// (like Avalonia's own Expander) so the view-model stays authoritative and re-pushes carry
/// the user's value rather than reverting it.
/// </summary>
[Collection("Headless UI")]
public sealed class TranscriptCollapsibleToggleTests
{
    [Theory]
    [InlineData(320, FlowDirection.LeftToRight)]
    [InlineData(640, FlowDirection.LeftToRight)]
    [InlineData(320, FlowDirection.RightToLeft)]
    public async Task RunningToolGroup_PreviewsStackWithinColumnAndToggleToFullDetails(
        double width, FlowDirection flowDirection)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var group = new ToolGroupItem("Working")
            {
                IsActive = true,
                Meta = "1/4 done - 3 running",
                ProgressValue = 25,
                ActivityPreview =
                [
                    new("Reading a file with a deliberately long name that exceeds the available column", "src\\ViewModels\\TranscriptBuilder.cs"),
                    new("Checking focused tests", "dotnet test tests\\Lumi.Tests --filter TranscriptBuilderToolGroupTests"),
                    new("Searching files", null),
                ],
            };
            group.ToolCalls.Add(new ToolCallItem("Read README", StrataAiToolCallStatus.Completed));
            group.ToolCalls.Add(new ToolCallItem("Reading file", StrataAiToolCallStatus.InProgress));
            group.ToolCalls.Add(new TerminalPreviewItem("Checking tests", "dotnet test", StrataAiToolCallStatus.InProgress));
            group.ToolCalls.Add(new ToolCallItem("Searching files", StrataAiToolCallStatus.InProgress));
            var chatView = new ChatView();
            var template = chatView.DataTemplates.Single(template => template.Match(group));
            var view = template.Build(group)!;
            view.DataContext = group;
            var window = new Window { Width = width, Height = 600, FlowDirection = flowDirection, Content = view };
            window.Show();
            try
            {
                await PumpAsync();
                window.UpdateLayout();
                var card = Assert.Single(view.GetVisualDescendants().OfType<StrataThink>());
                Assert.Null(card.HeaderExtra);
                Assert.Null(card.DisplayedContent);
                Assert.Same(card.PreviewContent, card.DisplayedPreviewContent);
                var rows = view.GetVisualDescendants().OfType<Grid>()
                    .Where(grid => grid.Classes.Contains("activity-preview-row")).ToArray();
                Assert.Equal(3, rows.Length);
                for (var index = 0; index < rows.Length; index++)
                {
                    var position = rows[index].TranslatePoint(default, window)!.Value;
                    var farEdge = rows[index].TranslatePoint(new Point(rows[index].Bounds.Width, 0), window)!.Value;
                    Assert.InRange(position.X, 0, width);
                    Assert.InRange(farEdge.X, 0, width + 1);
                    if (index > 0)
                    {
                        var previous = rows[index - 1].TranslatePoint(default, window)!.Value;
                        Assert.True(position.Y >= previous.Y + rows[index - 1].Bounds.Height);
                    }
                }
                Assert.Empty(view.GetVisualDescendants().OfType<StrataAiToolCall>());

                var previewPoint = rows[0].TranslatePoint(
                    new Point(rows[0].Bounds.Width / 2, rows[0].Bounds.Height / 2), window)!.Value;
                window.MouseDown(previewPoint, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(previewPoint, MouseButton.Left, RawInputModifiers.None);
                await PumpAsync();
                window.UpdateLayout();
                Assert.True(group.IsExpanded);
                Assert.Null(card.DisplayedPreviewContent);
                Assert.Same(card.Content, card.DisplayedContent);
                var details = view.GetVisualDescendants()
                    .Where(control => control is StrataAiToolCall or StrataTerminalPreview).ToArray();
                Assert.Equal(4, details.Length);
                for (var index = 1; index < details.Length; index++)
                {
                    var previous = details[index - 1].TranslatePoint(default, window)!.Value;
                    var position = details[index].TranslatePoint(default, window)!.Value;
                    var farEdge = details[index].TranslatePoint(new Point(details[index].Bounds.Width, 0), window)!.Value;
                    Assert.True(position.Y >= previous.Y + details[index - 1].Bounds.Height);
                    Assert.InRange(position.X, 0, width + 1);
                    Assert.InRange(farEdge.X, 0, width + 1);
                }

                card.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                view.DataContext = null;
                view.DataContext = group;
                await PumpAsync();
                Assert.False(group.IsExpanded);
                Assert.Same(card.PreviewContent, card.DisplayedPreviewContent);

                card.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
                await PumpAsync();
                window.UpdateLayout();
                Assert.True(group.IsExpanded);
                var header = card.GetVisualDescendants().OfType<Border>()
                    .First(border => border.Name == "PART_Header");
                var headerPoint = header.TranslatePoint(
                    new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
                window.MouseDown(headerPoint, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(headerPoint, MouseButton.Left, RawInputModifiers.None);
                Assert.False(group.IsExpanded);

                group.IsActive = false;
                group.ActivityPreview = [];
                group.Label = "Finished 4 actions";
                group.Meta = "4/4 done";
                group.ProgressValue = 100;
                await PumpAsync();
                window.UpdateLayout();
                Assert.Null(card.DisplayedPreviewContent);
                Assert.Null(card.DisplayedContent);
                Assert.DoesNotContain(":activity", card.Classes);
                Assert.DoesNotContain(view.GetVisualDescendants().OfType<Grid>(),
                    grid => grid.Classes.Contains("activity-preview-row"));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ToolGroupTemplate_SingleToolRendersAloneUntilSecondToolArrives()
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var group = new ToolGroupItem("Finished") { Meta = "1/1 done" };
            var first = new ToolCallItem("Read file", StrataAiToolCallStatus.InProgress);
            group.ToolCalls.Add(first);
            var chatView = new ChatView();
            var template = chatView.DataTemplates.Single(template => template.Match(group));
            var view = template.Build(group)!;
            view.DataContext = group;
            var window = new Window { Width = 800, Height = 500, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();
                var wrapper = Assert.Single(view.GetVisualDescendants().OfType<StrataThink>());
                Assert.False(wrapper.IsVisible);
                var tool = Assert.Single(view.GetVisualDescendants().OfType<StrataAiToolCall>());
                Assert.Equal(StrataAiToolCallStatus.InProgress, tool.Status);

                first.Status = StrataAiToolCallStatus.Completed;
                window.UpdateLayout();
                Assert.False(wrapper.IsVisible);
                Assert.Equal(StrataAiToolCallStatus.Completed, tool.Status);

                tool.IsExpanded = true;
                window.UpdateLayout();
                Assert.True(first.IsExpanded);
                group.ToolCalls.Add(new ToolCallItem("Search files", StrataAiToolCallStatus.InProgress));
                window.UpdateLayout();
                Assert.True(wrapper.IsVisible);
                Assert.True(wrapper.IsExpanded);
                Assert.True(first.IsExpanded);
                Assert.True(view.GetVisualDescendants().OfType<StrataAiToolCall>()
                    .Single(card => ReferenceEquals(card.DataContext, first)).IsExpanded);
                Assert.Null(group.SingleTool);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private sealed class ExpandVm : ObservableObject
    {
        private bool _isExpanded;

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }
    }

    [Fact]
    public Task StrataThink_CollapsePersistsAcrossDataContextChurn() =>
        AssertCollapsePersists(() =>
        {
            var think = new StrataThink { Label = "7 sources" };
            think.Bind(StrataThink.IsExpandedProperty, new Binding(nameof(ExpandVm.IsExpanded)));
            return think;
        });

    [Fact]
    public Task StrataAiToolCall_CollapsePersistsAcrossDataContextChurn() =>
        AssertCollapsePersists(() =>
        {
            var toolCall = new StrataAiToolCall { ToolName = "search_code" };
            toolCall.Bind(StrataAiToolCall.IsExpandedProperty, new Binding(nameof(ExpandVm.IsExpanded)));
            return toolCall;
        });

    [Fact]
    public Task StrataTerminalPreview_CollapsePersistsAcrossDataContextChurn() =>
        AssertCollapsePersists(() =>
        {
            var terminal = new StrataTerminalPreview { Command = "ls -la", Output = "file.txt" };
            terminal.Bind(StrataTerminalPreview.IsExpandedProperty, new Binding(nameof(ExpandVm.IsExpanded)));
            return terminal;
        });

    [Fact]
    public Task StrataTurnSummary_CollapsePersistsAcrossDataContextChurn() =>
        AssertCollapsePersists(() =>
        {
            var summary = new StrataTurnSummary { Label = "3 steps" };
            summary.Bind(StrataTurnSummary.IsExpandedProperty, new Binding(nameof(ExpandVm.IsExpanded)));
            return summary;
        });

    // Mirrors how TranscriptTurnControl hosts an item: build the view, assign DataContext, and
    // later churn that DataContext (release -> re-realize) while the item stays expanded in the VM.
    private static async Task AssertCollapsePersists(Func<TemplatedControl> buildView)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(async () =>
        {
            var vm = new ExpandVm { IsExpanded = true };

            var view = buildView();
            view.DataContext = vm;

            var window = new Window { Width = 480, Height = 320, Content = view };
            window.Show();
            await PumpAsync();

            Assert.True(GetIsExpanded(view), "View should start expanded from the bound VM value.");

            // User clicks the header to collapse (the control toggles IsExpanded on itself).
            SetIsExpanded(view, false);
            await PumpAsync();

            // TwoWay binding must have written the collapse back to the VM.
            Assert.False(vm.IsExpanded, "Collapsing the control should update the bound VM (TwoWay).");

            // Simulate the hosting churning DataContext (ClearItemHost -> CreateItemHost / reconcile).
            view.DataContext = null;
            await PumpAsync();
            view.DataContext = vm;
            await PumpAsync();

            Assert.False(GetIsExpanded(view), "Collapse must survive a DataContext re-assignment, not revert to expanded.");
            Assert.False(vm.IsExpanded);

            window.Close();
        }, CancellationToken.None);
    }

    private static bool GetIsExpanded(TemplatedControl view) => view switch
    {
        StrataThink think => think.IsExpanded,
        StrataAiToolCall toolCall => toolCall.IsExpanded,
        StrataTerminalPreview terminal => terminal.IsExpanded,
        StrataTurnSummary summary => summary.IsExpanded,
        _ => throw new InvalidOperationException($"Unhandled view type {view.GetType().Name}.")
    };

    private static void SetIsExpanded(TemplatedControl view, bool value)
    {
        switch (view)
        {
            case StrataThink think:
                think.IsExpanded = value;
                break;
            case StrataAiToolCall toolCall:
                toolCall.IsExpanded = value;
                break;
            case StrataTerminalPreview terminal:
                terminal.IsExpanded = value;
                break;
            case StrataTurnSummary summary:
                summary.IsExpanded = value;
                break;
            default:
                throw new InvalidOperationException($"Unhandled view type {view.GetType().Name}.");
        }
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
