using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

// Regression guard for the cumulative UI/render leak reported as "after a while animations break,
// the nav menu disappears, everything slows down". With a UIA client active, Avalonia created and
// then permanently retained an AutomationPeer + Win32 AutomationNode for every recycled transcript
// message control, pinning thousands of detached control subtrees and their composition visuals
// until the render thread was starved. The fix makes TranscriptTurnControl an automation LEAF so
// per-message peers are never created. These tests lock in that contract.
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

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    }
}
