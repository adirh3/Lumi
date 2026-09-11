using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
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
