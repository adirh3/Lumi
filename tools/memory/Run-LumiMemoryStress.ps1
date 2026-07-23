[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Cycles = 6,

    [ValidateRange(0, 20)]
    [int]$WarmupCycles = 2,

    [ValidateRange(1, 200)]
    [int]$ActionsPerCycle = 12,

    [ValidateRange(0, 5000)]
    [int]$SettleMilliseconds = 100,

    [ValidateRange(1, 10)]
    [int]$GcPasses = 4,

    [ValidateRange(1, 4096)]
    [int]$MaxManagedGrowthMb = 24,

    [ValidateRange(1, 1024)]
    [int]$MaxManagedSlopeMb = 2,

    [string[]]$Scenarios = @(),

    [switch]$KeepOpen,

    [switch]$NoBuild,

    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-DotnetChecked {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$projectPath = Join-Path $repoRoot 'src\Lumi\Lumi.csproj'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'diagnostics\memory'
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDirectory = Join-Path $OutputRoot "$timestamp-memory-stress"
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$runDirectory = (Resolve-Path $runDirectory).Path
$reportPath = Join-Path $runDirectory 'memory-report.json'

if (-not $NoBuild) {
    Invoke-DotnetChecked @('build', $projectPath, '--configuration', 'Debug', '--nologo')
}

$appArgs = @(
    '--memory-stress-harness',
    '--memory-cycles', "$Cycles",
    '--memory-warmup', "$WarmupCycles",
    '--memory-actions', "$ActionsPerCycle",
    '--memory-settle-ms', "$SettleMilliseconds",
    '--memory-gc-passes', "$GcPasses",
    '--memory-max-growth-mb', "$MaxManagedGrowthMb",
    '--memory-max-slope-mb', "$MaxManagedSlopeMb",
    '--memory-output', $reportPath
)

if ($Scenarios.Count -gt 0) {
    $appArgs += @('--memory-filter', ($Scenarios -join ','))
}

if ($KeepOpen) {
    $appArgs += '--memory-keep-open'
}

$runArgs = @(
    'run',
    '--project', $projectPath,
    '--configuration', 'Debug',
    '--no-build',
    '--'
) + $appArgs

Write-Host "Running Lumi memory stress harness."
Write-Host "Artifacts: $runDirectory"
Write-Host ""

Push-Location $repoRoot
try {
    & dotnet @runArgs
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if (Test-Path $reportPath) {
    Write-Host ""
    Write-Host "Memory report: $reportPath"
}

if ($exitCode -eq 3) {
    Write-Host "Memory gate failed. Use the report to choose a scenario, then collect a gcdump/process dump if a GC root is needed."
}
elseif ($exitCode -ne 0) {
    Write-Host "Memory harness failed with exit code $exitCode."
}

exit $exitCode
