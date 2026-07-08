using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

// Regression guard for the cumulative UI/render leak reported as "after a while animations break,
// the nav menu disappears, everything slows down". With a UIA client active, Avalonia created and
// then permanently retained an AutomationPeer + Win32 AutomationNode for every recycled transcript
// message control, pinning thousands of detached control subtrees and their composition visuals
// until the render thread was starved. The fix makes both the transcript container
// (TranscriptItemsControl) and each TranscriptTurnControl automation LEAVES so per-message peers are
// never created for the churning content. These tests lock in that contract.
[Collection("Headless UI")]
public sealed class TranscriptAutomationPeerTests
{
    private sealed class VisualTranscriptItem : TranscriptItem
    {
        public VisualTranscriptItem(string stableId, double desiredHeight, string text)
            : base(stableId)
        {
            DesiredHeight = desiredHeight;
            Text = text;
        }

        public double DesiredHeight { get; }
        public string Text { get; }
    }

    [Fact]
    public async Task TranscriptTurnControl_RealizesContentButExposesNoAutomationChildren()
    {
        using var session = HeadlessTestSession.Start();

        var renderedBorderCount = 0;
        var automationChildCount = -1;
        var hasPeer = false;

        await session.Dispatch(async () =>
        {
            var turn = new TranscriptTurn("turn:auto");
            turn.Items.Add(new VisualTranscriptItem("item:0000", 64, "first message"));
            turn.Items.Add(new VisualTranscriptItem("item:0001", 64, "second message"));

            var control = new TranscriptTurnControl { Turn = turn };
            var host = new StackPanel();
            host.Children.Add(control);

            var window = new Window
            {
                Width = 480,
                Height = 320,
                Content = host,
            };
            window.DataTemplates.Add(new FuncDataTemplate<VisualTranscriptItem>((item, _) => new Border
            {
                Height = item.DesiredHeight,
                Child = new TextBlock { Text = item.Text },
            }));

            window.Show();
            await PumpAsync();

            // Force the deferred item host to build, then pump a few layout cycles so the per-message
            // subtree (StackPanel host -> Border -> TextBlock) is actually inflated into the visual
            // tree. Without this the automation-children assertion below could pass vacuously.
            control.RealizePendingHost();
            for (var i = 0; i < 4; i++)
                await PumpAsync();

            // The turn really materialized its per-message visual subtree...
            renderedBorderCount = control.GetVisualDescendants().OfType<Border>().Count();

            // ...yet its automation peer reports zero children, so UIA never descends into (and never
            // pins) the recycled message controls.
            var peer = ControlAutomationPeer.CreatePeerForElement(control);
            hasPeer = peer is not null;
            automationChildCount = peer!.GetChildren().Count;

            window.Close();
        }, CancellationToken.None);

        Assert.True(renderedBorderCount > 0, "Transcript turn should realize its visual content.");
        Assert.True(hasPeer, "Transcript turn should still expose an automation peer (it is a node).");
        Assert.Equal(0, automationChildCount);
    }

    // The per-turn leaf alone is not enough: an ItemsControl's default ItemsControlAutomationPeer
    // creates per-item wrapper peers that walk the item container's visual children directly, so a UIA
    // walk still materializes peers/nodes for the churning turns and their message content. Pruning at
    // the STABLE container (TranscriptItemsControl) — which is reused across every transcript rebuild —
    // is what actually stops the unbounded automation-node growth. This test uses a plain Border item
    // template so it proves the container prunes regardless of what the items themselves expose.
    [Fact]
    public async Task TranscriptItemsControl_RealizesItemsButExposesNoAutomationChildren()
    {
        using var session = HeadlessTestSession.Start();

        var renderedItemCount = 0;
        var automationChildCount = -1;
        var hasPeer = false;

        await session.Dispatch(async () =>
        {
            var turns = new ObservableCollection<TranscriptTurn>
            {
                new("turn:0000"),
                new("turn:0001"),
                new("turn:0002"),
            };

            var items = new TranscriptItemsControl
            {
                ItemsSource = turns,
                ItemTemplate = new FuncDataTemplate<TranscriptTurn>((turn, _) => new Border
                {
                    Height = 40,
                    Child = new TextBlock { Text = turn.StableId },
                }),
            };
            items.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = 8 });

            // Host the container inside a StrataChatShell (its ScrollViewer drives the measure pass
            // that actually generates the item containers), matching the real ChatView layout.
            var shell = new StrataChatShell
            {
                Header = new TextBlock { Text = "Transcript Test" },
                Transcript = items,
                Composer = new Border { Height = 48 },
            };

            var window = new Window
            {
                Width = 480,
                Height = 320,
                Content = shell,
            };

            window.Show();
            await PumpAsync();
            for (var i = 0; i < 4; i++)
                await PumpAsync();

            // Force a full layout + render pass so the ItemsControl actually generates its item
            // containers (otherwise the realized-content assertion below could pass vacuously).
            _ = window.CaptureRenderedFrame();
            await PumpAsync();

            // The container really materialized its item visuals (one Border per turn)...
            renderedItemCount = items.GetVisualDescendants().OfType<Border>().Count();

            // ...yet its automation peer reports zero children, so a UIA walk never descends into (and
            // never pins) the recycled turn/message controls.
            var peer = ControlAutomationPeer.CreatePeerForElement(items);
            hasPeer = peer is not null;
            automationChildCount = peer!.GetChildren().Count;

            window.Close();
        }, CancellationToken.None);

        Assert.True(renderedItemCount > 0, "Transcript container should realize its item content.");
        Assert.True(hasPeer, "Transcript container should still expose an automation peer (labelled region).");
        Assert.Equal(0, automationChildCount);
    }

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }
}
