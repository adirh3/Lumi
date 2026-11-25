using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;

namespace Lumi.Mobile.Views;

public sealed class AnswerViewportPanel : StackPanel
{
    private AssistantItemViewModel? _answer;
    private Rect _viewport;

    public AnswerViewportPanel()
    {
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            UpdateDemand();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _answer?.SetInlineImageDemand(false);
        _answer = DataContext as AssistantItemViewModel;
        UpdateDemand();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _viewport = default;
        _answer?.SetInlineImageDemand(false);
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateDemand() =>
        _answer?.SetInlineImageDemand(_viewport.Width > 0 && _viewport.Height > 0
                                      && _viewport.Intersects(new Rect(Bounds.Size)));
}
