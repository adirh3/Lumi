using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Lumi.ViewModels;

namespace Lumi.Views.Management;

/// <summary>The read-only page of a skill, Lumi or MCP server that comes from a plugin or Copilot.</summary>
public partial class ExternalCapabilityDetail : UserControl
{
    public ExternalCapabilityDetail()
    {
        AvaloniaXamlLoader.Load(this);
        ManagementPage.ResetScrollOn(this, "ExternalDetailScroll", nameof(ExternalCapabilitiesViewModel.OpenItem));
    }
}
