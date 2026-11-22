using Avalonia.Controls;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class AttentionInboxView : UserControl
{
    public AttentionInboxView()
    {
        InitializeComponent();
        AddHandler(StrataInteractiveBlock.RequestedEvent, OnInteractiveRequest);
    }

    private void OnInteractiveRequest(object? sender, InteractiveRequestEventArgs request)
    {
        request.Handled = true;
        if (request.Source is Control { DataContext: InputRequestItem input }
            && DataContext is AttentionInboxViewModel inbox
            && inbox.Requests.FirstOrDefault(entry => ReferenceEquals(entry.Item, input)) is { } entry)
        {
            request.Completion = entry.RequestInteractionAsync(request.Definition.Id, request.StateJson, request.Action);
        }
        else
        {
            request.Completion = Task.FromResult(new InteractiveActionResult(false,
                InteractiveLabels.Current["inputExpired"]));
        }
    }
}
