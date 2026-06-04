[CmdletBinding(DefaultParameterSetName = 'ByProcessId')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'ByProcessId')]
    [Alias('Pid')]
    [int]$ProcessId,

    [Parameter(ParameterSetName = 'ByProcessName')]
    [string]$ProcessName = 'Lumi',

    [string]$Scenario = 'manual',

    [switch]$CollectCounters,

    [ValidateRange(1, 86400)]
    [int]$CountersDurationSeconds = 30,

    [ValidateRange(1, 60)]
    [int]$RefreshIntervalSeconds = 1,

    [switch]$IncludeProcessDump,

    [ValidateSet('Full', 'Heap', 'Mini', 'Triage')]
    [string]$DumpType = 'Heap',

    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function ConvertTo-SafeName {
    param([string]$Value)

    $safe = $Value -replace '[^A-Za-z0-9_.-]', '-'
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return 'manual'
    }

    return $safe
}

function ConvertTo-DurationText {
    param([int]$Seconds)

    $duration = [TimeSpan]::FromSeconds($Seconds)
    return '{0:00}:{1:00}:{2:00}:{3:00}' -f $duration.Days, $duration.Hours, $duration.Minutes, $duration.Seconds
}

function Invoke-DotnetChecked {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Resolve-TargetProcess {
    if ($PSCmdlet.ParameterSetName -eq 'ByProcessId') {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if (-not $process) {
            throw "No process with PID $ProcessId is running."
        }

        return $process
    }

    $matches = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Sort-Object -Property Id -Descending)
    if ($matches.Count -eq 0) {
        throw "No process named '$ProcessName' is running. Pass -ProcessId for a debug-hosted process."
    }

    if ($matches.Count -gt 1) {
        $ids = ($matches | ForEach-Object { $_.Id }) -join ', '
        throw "Multiple '$ProcessName' processes are running ($ids). Pass -ProcessId to choose one."
    }

    return $matches[0]
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$manifest = Join-Path $repoRoot 'dotnet-tools.json'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'diagnostics\memory'
}

$targetProcess = Resolve-TargetProcess
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDirectory = Join-Path $OutputRoot "$timestamp-$(ConvertTo-SafeName $Scenario)-pid$($targetProcess.Id)"
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$runDirectory = (Resolve-Path $runDirectory).Path

Invoke-DotnetChecked @('tool', 'restore', '--tool-manifest', $manifest)

$metadata = [ordered]@{
    scenario = $Scenario
    capturedAt = (Get-Date).ToString('o')
    process = @{
        id = $targetProcess.Id
        name = $targetProcess.ProcessName
        path = $targetProcess.Path
    }
    outputDirectory = $runDirectory
    warning = 'Diagnostic captures can contain chat contents, file paths, URLs, and secrets. Keep diagnostics\memory local and do not commit or share raw dumps.'
}

if ($CollectCounters) {
    $counterPath = Join-Path $runDirectory 'system-runtime-counters.csv'
    $duration = ConvertTo-DurationText $CountersDurationSeconds
    Invoke-DotnetChecked @(
        'tool', 'run', 'dotnet-counters', '--',
        'collect',
        '--process-id', "$($targetProcess.Id)",
        '--refresh-interval', "$RefreshIntervalSeconds",
        '--format', 'csv',
        '--output', $counterPath,
        '--counters', 'System.Runtime',
        '--duration', $duration
    )
    $metadata.counters = @{
        path = $counterPath
        duration = $duration
        refreshIntervalSeconds = $RefreshIntervalSeconds
    }
}

$gcdumpPath = Join-Path $runDirectory 'heap.gcdump'
Invoke-DotnetChecked @(
    'tool', 'run', 'dotnet-gcdump', '--',
    'collect',
    '--process-id', "$($targetProcess.Id)",
    '--output', $gcdumpPath,
    '--timeout', '60'
)
$metadata.gcdump = $gcdumpPath

$heapStatPath = Join-Path $runDirectory 'heapstat.txt'
$heapStatOutput = & dotnet tool run dotnet-gcdump -- report $gcdumpPath 2>&1
$heapStatOutput | Set-Content -Path $heapStatPath -Encoding utf8
if ($LASTEXITCODE -ne 0) {
    throw "dotnet-gcdump report failed with exit code $LASTEXITCODE. See '$heapStatPath'."
}
$metadata.heapStat = $heapStatPath

if ($IncludeProcessDump) {
    $dumpPath = Join-Path $runDirectory "process-$DumpType.dmp"
    Invoke-DotnetChecked @(
        'tool', 'run', 'dotnet-dump', '--',
        'collect',
        '--process-id', "$($targetProcess.Id)",
        '--type', $DumpType,
        '--output', $dumpPath
    )
    $metadata.dump = @{
        path = $dumpPath
        type = $DumpType
    }
}

$metadataPath = Join-Path $runDirectory 'snapshot-metadata.json'
$metadata | ConvertTo-Json -Depth 6 | Set-Content -Path $metadataPath -Encoding utf8

Write-Host "Memory snapshot collected."
Write-Host "Artifacts: $runDirectory"
Write-Host "GC dump: $gcdumpPath"
Write-Host "Heap stats: $heapStatPath"
if ($IncludeProcessDump) {
    Write-Host "Process dump: $dumpPath"
    Write-Host ""
    Write-Host "Next deep-dive command:"
    Write-Host "  dotnet tool run dotnet-dump -- analyze `"$dumpPath`""
}
else {
    Write-Host ""
    Write-Host "Use -IncludeProcessDump when you need dotnet-dump gcroot analysis."
}
