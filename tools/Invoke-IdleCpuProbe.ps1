param(
    [Parameter(Mandatory)][string]$Variant,
    [switch]$Bare,
    [ValidateSet('default', 'software')][string]$Renderer = 'default'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [System.IO.Path]::Combine($root, 'IdleProbeResults', $Variant)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$app = @(Get-ChildItem ([System.IO.Path]::Combine($root, 'src', 'Lumi', 'bin', 'Debug')) -Filter Lumi.dll -File -Recurse)
if ($app.Count -ne 1) {
    throw "Expected exactly one built Lumi assembly, found $($app.Count)."
}

$start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = $root
$start.ArgumentList.Add($app[0].FullName)
$start.ArgumentList.Add('--skip-onboarding')
$start.Environment['LUMI_IDLE_CPU_PROBE'] = '1'
$start.Environment['LUMI_IDLE_CPU_BARE'] = $(if ($Bare) { '1' } else { '0' })
$start.Environment['LUMI_IDLE_CPU_RENDERER'] = $Renderer
$start.Environment['LUMI_IDLE_CPU_VARIANT'] = $Variant
$start.Environment['LUMI_IDLE_CPU_OUTPUT'] = $output
$start.Environment['LUMI_APPDATA_DIR'] = Join-Path ([System.IO.Path]::GetTempPath()) "Lumi-idle-probe-$([guid]::NewGuid())"

$process = [System.Diagnostics.Process]::Start($start)
if ($null -eq $process) { throw 'Could not start the idle probe.' }
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
try {
    if (-not $process.WaitForExit(180000)) {
        Stop-Process -Id $process.Id -Force
        throw "Idle probe PID $($process.Id) exceeded its three-minute limit."
    }
    if ($process.ExitCode -ne 0) {
        throw "Idle probe failed with exit code $($process.ExitCode). See $output."
    }
}
finally {
    $stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $output 'stdout.log')
    $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $output 'stderr.log')
    $process.Dispose()
}
if (-not (Test-Path (Join-Path $output 'report.json'))) {
    throw "No CPU report was produced in $output."
}
Get-Content (Join-Path $output 'report.json')
