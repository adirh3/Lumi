#requires -Version 5.1
<#
.SYNOPSIS
Starts a synthetic, local WPF UI automation target.
.DESCRIPTION
Run with system Windows PowerShell 5.1 -STA, not pwsh. The caller supplies a unique
visible title and a new state path, and owns the returned OS process. Nothing leaves
this process. The Orders list virtualizes its 2,000 rows, so offscreen rows do not
exist in the UI Automation tree until they are realized. State is recorded by the
fixture's own events when Apply dispatch is clicked.
.EXAMPLE
powershell.exe -NoProfile -STA -File .\Start-UIAutomationWpfFixture.ps1 -Title "Lumi WPF bench" -StatePath C:\Temp\wpf-001\state.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Title,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$StatePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
if ($PSVersionTable.PSEdition -ne 'Desktop' -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Use system Windows PowerShell 5.1 with -STA.'
}
$script:artifactPath = [IO.Path]::GetFullPath($StatePath)
if ([IO.File]::Exists($script:artifactPath)) {
    throw 'StatePath must be new; refusing to overwrite an earlier fixture.'
}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($script:artifactPath))
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$script:state = [ordered]@{
    schemaVersion = 1
    title = $Title
    processId = [Diagnostics.Process]::GetCurrentProcess().Id
    startedAt = [DateTimeOffset]::UtcNow.ToString('o')
    ready = $false
    closed = $false
    sequence = 0
    lastEvent = 'created'
    wpf = [ordered]@{
        totalOrders = 2000; applied = $false; applyCount = 0; selectedOrder = $null
        discount = $null; priority = $false; warehouse = $null; advancedExpanded = $false
    }
}

function Save-FixtureState {
    param([string]$EventName)
    $script:state.sequence++
    $script:state.lastEvent = $EventName
    $json = ConvertTo-Json -InputObject $script:state -Depth 6
    [IO.File]::WriteAllText($script:artifactPath, $json, [Text.UTF8Encoding]::new($false))
}

$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="760" Height="620" WindowStartupLocation="CenterScreen" ResizeMode="NoResize"
        AutomationProperties.AutomationId="WpfFixture">
  <DockPanel Margin="16">
    <TextBlock DockPanel.Dock="Top" FontSize="16" Margin="0,0,0,12" Text="Dispatch console - synthetic data only" />
    <TextBlock x:Name="Status" DockPanel.Dock="Bottom" Margin="0,12,0,0" AutomationProperties.AutomationId="Status"
               Text="No dispatch applied." />
    <Grid>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="340" />
        <ColumnDefinition Width="*" />
      </Grid.ColumnDefinitions>
      <DockPanel Grid.Column="0">
        <TextBlock DockPanel.Dock="Top" Margin="0,0,0,4" Text="Orders" />
        <ListBox x:Name="Orders" AutomationProperties.AutomationId="Orders" AutomationProperties.Name="Orders"
                 VirtualizingStackPanel.IsVirtualizing="True" VirtualizingStackPanel.VirtualizationMode="Recycling" />
      </DockPanel>
      <StackPanel Grid.Column="1" Margin="16,0,0,0">
        <TextBlock Text="Discount (%)" />
        <Slider x:Name="Discount" AutomationProperties.AutomationId="Discount" AutomationProperties.Name="Discount (%)"
                Minimum="0" Maximum="50" SmallChange="1" LargeChange="5" Margin="0,4,0,4" />
        <TextBlock x:Name="DiscountValue" AutomationProperties.AutomationId="DiscountValue" Text="Discount: 0%" />
        <Expander x:Name="Advanced" AutomationProperties.AutomationId="Advanced" Header="Advanced options" Margin="0,16,0,0">
          <StackPanel Margin="8">
            <CheckBox x:Name="Priority" AutomationProperties.AutomationId="Priority" Content="Priority handling" />
            <TextBlock Margin="0,8,0,2" Text="Warehouse" />
            <ComboBox x:Name="Warehouse" AutomationProperties.AutomationId="Warehouse" AutomationProperties.Name="Warehouse"
                      SelectedIndex="0">
              <ComboBoxItem Content="Bristol" />
              <ComboBoxItem Content="Leeds" />
              <ComboBoxItem Content="York" />
            </ComboBox>
          </StackPanel>
        </Expander>
        <Button x:Name="Apply" AutomationProperties.AutomationId="Apply" Content="Apply dispatch" Margin="0,16,0,0"
                Padding="8,4" HorizontalAlignment="Left" />
      </StackPanel>
    </Grid>
  </DockPanel>
</Window>
'@
$script:window = [Windows.Markup.XamlReader]::Parse($xaml)
$script:window.Title = $Title
foreach ($name in @('Status', 'Orders', 'Discount', 'DiscountValue', 'Advanced', 'Priority', 'Warehouse', 'Apply')) {
    Set-Variable -Scope Script -Name $name -Value $script:window.FindName($name)
}
$customers = @('Northwind Traders', 'Fabrikam Retail', 'Adventure Works', 'Tailspin Toys', 'Wide World Importers', 'Litware Labs')
$script:Orders.ItemsSource = [string[]]@(1..2000 | ForEach-Object {
    if ($_ -eq 1873) { 'Order 1873 - Harbor Books' }
    else { 'Order {0:D4} - {1}' -f $_, $customers[$_ % $customers.Count] }
})
$script:Discount.Add_ValueChanged({
    $script:DiscountValue.Text = 'Discount: ' + [Math]::Round($script:Discount.Value) + '%'
})
$script:Advanced.Add_Expanded({
    $script:state.wpf.advancedExpanded = $true
    Save-FixtureState 'advanced-expanded'
})
$script:Apply.Add_Click({
    $selection = [string]$script:Orders.SelectedItem
    if (-not $selection) {
        $script:Status.Text = 'Select an order first.'
        return
    }
    $script:state.wpf.applied = $true
    $script:state.wpf.applyCount++
    $script:state.wpf.selectedOrder = $selection
    $script:state.wpf.discount = [int][Math]::Round($script:Discount.Value)
    $script:state.wpf.priority = [bool]$script:Priority.IsChecked
    $script:state.wpf.warehouse = [string]$script:Warehouse.Text
    Save-FixtureState 'dispatch-applied'
    $script:Status.Text = 'Dispatch applied to ' + $selection + '.'
})
$script:window.Add_ContentRendered({
    $script:state.ready = $true
    Save-FixtureState 'ready'
})
$script:window.Add_Closed({
    $script:state.ready = $false
    $script:state.closed = $true
    Save-FixtureState 'closed'
})
[void]$script:window.ShowDialog()
