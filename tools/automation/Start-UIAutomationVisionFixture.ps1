#requires -Version 5.1
<#
.SYNOPSIS
Shows a disposable, painted-only target for screenshot/coordinate-click validation.
.DESCRIPTION
Run with Windows PowerShell 5.1 -STA. The visual code is painted, not exposed as
control text. StatePath is for the independent test controller, not the model.
Only the supplied state file is written. Close only the process you started.
The benchmark runner includes this fixture in FixtureOnly; physical clicks
require an unlocked interactive desktop. The default canvas and blue Confirm
target are unchanged for direct capture tests. Gestures adds green Double click
and orange Right click targets on that same canvas; counts come from actual
MouseDoubleClick and right-button MouseUp events, not inferred single clicks.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Title,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$StatePath,
    [switch]$Gestures
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
if ($PSVersionTable.PSEdition -ne 'Desktop' -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Use system Windows PowerShell 5.1 with -STA.'
}
$script:artifactPath = [IO.Path]::GetFullPath($StatePath)
if ([IO.File]::Exists($script:artifactPath)) { throw 'StatePath must be new; refusing to overwrite an earlier fixture.' }
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($script:artifactPath))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$script:code = [guid]::NewGuid().ToString('N').Substring(0, 7).ToUpperInvariant()
$script:clicks = 0
$script:outsideClicks = 0
$script:doubleClicks = 0
$script:rightClicks = 0
$script:target = [Drawing.Rectangle]::new(420, 190, 190, 76)
$script:doubleTarget = [Drawing.Rectangle]::new(280, 120, 175, 52)
$script:rightTarget = [Drawing.Rectangle]::new(480, 120, 160, 52)
$form = [Windows.Forms.Form]::new()
$form.Text = $Title
$form.Name = 'VisionFixture'
$form.AccessibleName = $Title
$form.ClientSize = [Drawing.Size]::new(680, 360)
$form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
$form.Location = [Drawing.Point]::new(120, 180)
$form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedDialog
$form.MaximizeBox = $false
$canvas = [Windows.Forms.Panel]::new()
$canvas.Dock = [Windows.Forms.DockStyle]::Fill
$canvas.BackColor = [Drawing.Color]::FromArgb(246, 248, 252)
$canvas.AccessibleName = 'Painted test canvas'
$form.Controls.Add($canvas)
$script:titleFont = [Drawing.Font]::new('Segoe UI', 22, [Drawing.FontStyle]::Bold)
$script:textFont = [Drawing.Font]::new('Segoe UI', 14)
$script:codeFont = [Drawing.Font]::new('Consolas', 28, [Drawing.FontStyle]::Bold)

function Save-State([bool]$Ready) {
    $state = [ordered]@{
        title = $Title; processId = $PID; ready = $Ready; closed = -not $Ready
        visualCode = $script:code; confirmed = ($script:clicks -gt 0); clicks = $script:clicks
        outsideClicks = $script:outsideClicks
        gesturesEnabled = [bool]$Gestures; doubleClicks = $script:doubleClicks; rightClicks = $script:rightClicks
    }
    [IO.File]::WriteAllText($script:artifactPath,
        ($state | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
$canvas.Add_Paint({
    $g = $_.Graphics
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.DrawString('Desktop vision check', $script:titleFont, [Drawing.Brushes]::MidnightBlue, 30, 26)
    $instruction = if ($Gestures) { 'Double-click green and right-click orange. Leave blue untouched.' } else { 'Read the code, then click the blue Confirm target.' }
    $g.DrawString($instruction, $script:textFont, [Drawing.Brushes]::DarkSlateGray, 32, 80)
    $g.DrawString($script:code, $script:codeFont, [Drawing.Brushes]::Black, 35, 158)
    $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(58, 70, 190))
    try { $g.FillRectangle($brush, $script:target) } finally { $brush.Dispose() }
    $label = if ($script:clicks -gt 0) { 'Confirmed' } else { 'Confirm' }
    $g.DrawString($label, $script:textFont, [Drawing.Brushes]::White, 460, 216)
    if ($Gestures) {
        $g.FillRectangle([Drawing.Brushes]::ForestGreen, $script:doubleTarget)
        $g.FillRectangle([Drawing.Brushes]::DarkOrange, $script:rightTarget)
        $doubleLabel = if ($script:doubleClicks -gt 0) { 'Double: ' + $script:doubleClicks } else { 'Double click' }
        $rightLabel = if ($script:rightClicks -gt 0) { 'Right: ' + $script:rightClicks } else { 'Right click' }
        $g.DrawString($doubleLabel, $script:textFont, [Drawing.Brushes]::White, 294, 135)
        $g.DrawString($rightLabel, $script:textFont, [Drawing.Brushes]::Black, 494, 135)
    }
    $footer = if ($Gestures) { "Double clicks: $script:doubleClicks; right clicks: $script:rightClicks" }
        elseif ($script:clicks -gt 0) { 'The correct target was clicked.' } else { 'No target has been clicked yet.' }
    $g.DrawString($footer, $script:textFont, [Drawing.Brushes]::DarkSlateGray, 32, 301)
})
$canvas.Add_MouseClick({
    if ($script:target.Contains($_.Location)) {
        $script:clicks++
        $canvas.Invalidate()
    }
    elseif (-not $Gestures -or -not ($script:doubleTarget.Contains($_.Location) -or $script:rightTarget.Contains($_.Location))) {
        $script:outsideClicks++
    }
    Save-State $true
})
$canvas.Add_MouseDoubleClick({
    if ($Gestures -and $_.Button -eq [Windows.Forms.MouseButtons]::Left -and $script:doubleTarget.Contains($_.Location)) {
        $script:doubleClicks++
        Save-State $true
        $canvas.Invalidate()
    }
})
$canvas.Add_MouseUp({
    if ($Gestures -and $_.Button -eq [Windows.Forms.MouseButtons]::Right -and $script:rightTarget.Contains($_.Location)) {
        $script:rightClicks++
        Save-State $true
        $canvas.Invalidate()
    }
})
$form.Add_Shown({ Save-State $true })
$form.Add_FormClosed({ Save-State $false })
try { [Windows.Forms.Application]::Run($form) }
finally {
    $script:titleFont.Dispose()
    $script:textFont.Dispose()
    $script:codeFont.Dispose()
    $form.Dispose()
}
