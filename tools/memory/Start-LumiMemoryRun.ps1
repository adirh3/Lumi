[CmdletBinding()]
param(
    [ValidateSet('Harness', 'App')]
    [string]$Mode = 'Harness',

    [string]$Scenario = 'manual',

    [switch]$CollectCounters,

    [ValidateRange(1, 86400)]
    [int]$CountersDurationSeconds = 300,

    [ValidateRange(1, 60)]
    [int]$RefreshIntervalSeconds = 1,

    [switch]$NoBuild,

    [string]$OutputRoot,

    [string[]]$AdditionalAppArgs = @()
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

function ConvertTo-ArgumentString {
    param([string[]]$Arguments)

    return ($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + ($_ -replace '"', '\"') + '"'
        }
        else {
            $_
        }
    }) -join ' '
}

function Invoke-DotnetChecked {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Start-CheckedProcess {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    $argumentString = ConvertTo-ArgumentString $Arguments
    return Start-Process -FilePath $FilePath -ArgumentList $argumentString -WorkingDirectory $WorkingDirectory -PassThru
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$manifest = Join-Path $repoRoot 'dotnet-tools.json'
$projectPath = Join-Path $repoRoot 'src\Lumi\Lumi.csproj'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'diagnostics\memory'
}

Invoke-DotnetChecked @('tool', 'restore', '--tool-manifest', $manifest)

if (-not $NoBuild) {
    Invoke-DotnetChecked @('build', $projectPath, '--configuration', 'Debug', '--nologo')
}

$isWindowsPlatform = if (Get-Variable -Name IsWindows -ErrorAction SilentlyContinue) { $IsWindows } else { $env:OS -eq 'Windows_NT' }
$targetFramework = if ($isWindowsPlatform) { 'net11.0-windows10.0.22621.0' } else { 'net11.0' }
$outputDirectory = Join-Path $repoRoot "src\Lumi\bin\Debug\$targetFramework"
$lumiExeItem = Get-ChildItem -Path $outputDirectory -Filter 'Lumi.exe' -File -Recurse -ErrorAction SilentlyContinue |
    Sort-Object -Property FullName |
    Select-Object -First 1
$lumiDllItem = Get-ChildItem -Path $outputDirectory -Filter 'Lumi.dll' -File -Recurse -ErrorAction SilentlyContinue |
    Sort-Object -Property FullName |
    Select-Object -First 1
$appArgs = @()

if ($Mode -eq 'Harness') {
    $appArgs += '--debug-agent-harness'
}

$appArgs += $AdditionalAppArgs

if ($lumiExeItem) {
    $filePath = $lumiExeItem.FullName
    $arguments = $appArgs
}
elseif ($lumiDllItem) {
    $filePath = 'dotnet'
    $arguments = @($lumiDllItem.FullName) + $appArgs
}
else {
    throw "Could not find built Lumi output in '$outputDirectory'."
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDirectory = Join-Path $OutputRoot "$timestamp-$(ConvertTo-SafeName $Scenario)"
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$runDirectory = (Resolve-Path $runDirectory).Path

$lumiProcess = Start-CheckedProcess -FilePath $filePath -Arguments $arguments -WorkingDirectory $repoRoot
Start-Sleep -Seconds 2
if ($lumiProcess.HasExited) {
    throw "Lumi exited immediately with code $($lumiProcess.ExitCode)."
}

$metadata = [ordered]@{
    scenario = $Scenario
    mode = $Mode
    startedAt = (Get-Date).ToString('o')
    processId = $lumiProcess.Id
    outputDirectory = $runDirectory
    command = @{
        filePath = $filePath
        arguments = $arguments
        workingDirectory = $repoRoot
    }
    warning = 'Diagnostic captures can contain chat contents, file paths, URLs, and secrets. Keep diagnostics\memory local and do not commit or share raw dumps.'
}

if ($CollectCounters) {
    $counterPath = Join-Path $runDirectory 'system-runtime-counters.csv'
    $duration = ConvertTo-DurationText $CountersDurationSeconds
    $counterArgs = @(
        'tool', 'run', 'dotnet-counters', '--',
        'collect',
        '--process-id', "$($lumiProcess.Id)",
        '--refresh-interval', "$RefreshIntervalSeconds",
        '--format', 'csv',
        '--output', $counterPath,
        '--counters', 'System.Runtime',
        '--duration', $duration
    )

    $counterProcess = Start-CheckedProcess -FilePath 'dotnet' -Arguments $counterArgs -WorkingDirectory $repoRoot
    $metadata.counters = @{
        processId = $counterProcess.Id
        path = $counterPath
        duration = $duration
        refreshIntervalSeconds = $RefreshIntervalSeconds
    }
}

$metadataPath = Join-Path $runDirectory 'run-metadata.json'
$metadata | ConvertTo-Json -Depth 6 | Set-Content -Path $metadataPath -Encoding utf8

Write-Host "Lumi started for memory debugging."
Write-Host "PID: $($lumiProcess.Id)"
Write-Host "Artifacts: $runDirectory"
Write-Host ""
Write-Host "Collect a snapshot:"
Write-Host "  .\tools\memory\Collect-LumiMemorySnapshot.ps1 -ProcessId $($lumiProcess.Id) -Scenario $Scenario"
Write-Host ""
Write-Host "Watch live counters:"
Write-Host "  .\tools\memory\Watch-LumiMemoryCounters.ps1 -ProcessId $($lumiProcess.Id)"
