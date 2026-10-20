using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Lumi.Views.Management;

/// <summary>A Customize section's read-only items, grouped by the plugin or Copilot location they come from.</summary>
public partial class ExternalCapabilitySections : UserControl
{
    public ExternalCapabilitySections()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
