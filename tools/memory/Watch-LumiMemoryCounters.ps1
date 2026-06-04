[CmdletBinding(DefaultParameterSetName = 'ByProcessId')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'ByProcessId')]
    [Alias('Pid')]
    [int]$ProcessId,

    [Parameter(ParameterSetName = 'ByProcessName')]
    [string]$ProcessName = 'Lumi',

    [ValidateRange(1, 60)]
    [int]$RefreshIntervalSeconds = 1,

    [string]$Counters = 'System.Runtime'
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

function Resolve-TargetProcessId {
    if ($PSCmdlet.ParameterSetName -eq 'ByProcessId') {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            throw "No process with PID $ProcessId is running."
        }

        return $ProcessId
    }

    $matches = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Sort-Object -Property Id -Descending)
    if ($matches.Count -eq 0) {
        throw "No process named '$ProcessName' is running. Pass -ProcessId for a debug-hosted process."
    }

    if ($matches.Count -gt 1) {
        $ids = ($matches | ForEach-Object { $_.Id }) -join ', '
        throw "Multiple '$ProcessName' processes are running ($ids). Pass -ProcessId to choose one."
    }

    return $matches[0].Id
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$manifest = Join-Path $repoRoot 'dotnet-tools.json'
$targetPid = Resolve-TargetProcessId

Invoke-DotnetChecked @('tool', 'restore', '--tool-manifest', $manifest)

& dotnet tool run dotnet-counters -- monitor `
    --process-id $targetPid `
    --refresh-interval $RefreshIntervalSeconds `
    --counters $Counters
