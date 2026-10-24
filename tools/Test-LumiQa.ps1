#requires -Version 7.2
<#
.SYNOPSIS
Runs deterministic component QA or the real headed Lumi workflows and process-restart check.
.DESCRIPTION
No live model accounts are used. Core runs Category=CoreQA in fresh desktop/mobile test hosts.
Headed runs Debug Lumi twice against a local scripted model, preserving only its isolated profile
between launches. Missing evidence, skipped component tests, incomplete scenarios and timeouts fail.
The output contains summary.json and per-suite TRX/log/JSON/PNG evidence. Profile data is not needed
for CI artifact upload. Run on Linux inside xvfb-run -a dbus-run-session.
#>
[CmdletBinding()]
param(
    [ValidateSet('Core', 'Headed')][string]$Suite = 'Core',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$ResultsDirectory = 'QaResults',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run = Join-Path ([IO.Path]::GetFullPath($ResultsDirectory)) (
    '{0}-{1}-{2}' -f $Suite.ToLowerInvariant(), [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8))
[void][IO.Directory]::CreateDirectory($run)
$started = [DateTimeOffset]::UtcNow
$results = [Collections.Generic.List[object]]::new()
$failure = $null

function Test-CoreProject {
    param([string]$Project, [string]$Name)
    $root = Join-Path $run $Name
    [void][IO.Directory]::CreateDirectory($root)
    $arguments = @(
        'test', (Join-Path $repo $Project), '-c', $Configuration, '--no-build', '--nologo',
        '--filter', 'Category=CoreQA',
        '--logger', 'trx;LogFileName=results.trx', '--results-directory', $root,
        '--blame-hang', '--blame-hang-timeout', '2m', '--blame-hang-dump-type', 'none')
    & dotnet @arguments *> (Join-Path $root 'test.log')
    $code = $LASTEXITCODE
    $path = Join-Path $root 'results.trx'
    if (-not (Test-Path -LiteralPath $path)) { throw "$Name produced no TRX (exit $code)." }
    [xml]$trx = Get-Content -LiteralPath $path -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $count = [int]$counters.total
    $passed = $code -eq 0 -and $count -gt 0 -and [int]$counters.executed -eq $count -and [int]$counters.passed -eq $count
    $results.Add([ordered]@{
        name = $Name; passed = $passed; tests = $count; executed = [int]$counters.executed
        failed = [int]$counters.failed; skipped = [int]$counters.notExecuted; exitCode = $code
    })
    Get-Content -LiteralPath (Join-Path $root 'test.log') | Write-Output
}

function Test-HeadedPhase {
    param([string]$Phase, [string]$Profile)
    $root = Join-Path $run $Phase
    [void][IO.Directory]::CreateDirectory($root)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $process.StartInfo.WorkingDirectory = $repo
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.Environment['LUMI_APPDATA_DIR'] = $Profile
    foreach ($argument in @('run', '--project', (Join-Path $repo 'src\Lumi\Lumi.csproj'),
        '-c', 'Debug', '--no-build', '--', '--qa-smoke', '--qa-output', $root)) {
        $process.StartInfo.ArgumentList.Add($argument)
    }
    if ($Phase -eq 'restart') { $process.StartInfo.ArgumentList.Add('--qa-verify-restart') }
    try {
        if (-not $process.Start()) { throw "Could not launch $Phase." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit(420000)
        if ($timedOut) {
            # Only the process tree started by this phase, never another Lumi instance.
            $process.Kill($true)
            $process.WaitForExit()
        }
        $text = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $root 'stdout.log'), $text)
        [IO.File]::WriteAllText((Join-Path $root 'stderr.log'), $errors)
        Write-Output $text
        if ($errors) { Write-Output $errors }
        if ($timedOut) { throw "$Phase exceeded seven minutes." }
        $reportPath = Join-Path $root 'results.json'
        if (-not (Test-Path -LiteralPath $reportPath)) { throw "$Phase produced no QA report (exit $($process.ExitCode))." }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $expected = @($report.expectedScenarios | Sort-Object -CaseSensitive)
        $actual = @($report.scenarios | ForEach-Object { $_.id } | Sort-Object -CaseSensitive)
        $passed = $process.ExitCode -eq 0 -and $report.complete -eq $true -and $expected.Count -gt 0 -and
            ($expected -join "`n") -ceq ($actual -join "`n") -and
            @($report.scenarios | Where-Object { $_.passed -ne $true }).Count -eq 0 -and
            $text.Contains('[RESULT] LUMI_QA_OK', [StringComparison]::Ordinal)
        $results.Add([ordered]@{
            name = $Phase; passed = $passed; scenarios = $actual.Count
            processId = $report.processId; exitCode = $process.ExitCode
        })
    }
    finally { $process.Dispose() }
}

Push-Location $repo
try {
    if (-not $NoBuild) {
        $target = if ($Suite -eq 'Core') { 'Lumi.slnx' } else { 'src\Lumi\Lumi.csproj' }
        $config = if ($Suite -eq 'Headed') { 'Debug' } else { $Configuration }
        & dotnet build $target -c $config --nologo --verbosity quiet *> (Join-Path $run 'build.log')
        if ($LASTEXITCODE -ne 0) { throw "QA build failed; see $(Join-Path $run 'build.log')." }
    }
    if ($Suite -eq 'Core') {
        foreach ($project in @(
            @{ Path = 'tests\Lumi.Tests\Lumi.Tests.csproj'; Name = 'desktop' },
            @{ Path = 'tests\Lumi.Mobile.Tests\Lumi.Mobile.Tests.csproj'; Name = 'mobile' })) {
            try { Test-CoreProject $project.Path $project.Name }
            catch {
                $results.Add([ordered]@{ name = $project.Name; passed = $false; error = $_.ToString() })
                Write-Warning $_
            }
        }
    }
    else {
        $profile = Join-Path $run 'profile'
        [void][IO.Directory]::CreateDirectory($profile)
        foreach ($phase in @('workflows', 'restart')) {
            try { Test-HeadedPhase $phase $profile }
            catch {
                $results.Add([ordered]@{ name = $phase; passed = $false; error = $_.ToString() })
                Write-Warning $_
            }
        }
    }
}
catch { $failure = $_.ToString(); Write-Warning $_ }
finally {
    Pop-Location
    $complete = $null -eq $failure -and $results.Count -eq 2 -and
        @($results | Where-Object { -not $_.passed }).Count -eq 0
    $summary = [ordered]@{
        schemaVersion = 1; suite = $Suite; complete = $complete
        commit = $env:GITHUB_SHA
        os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        startedAt = $started.ToString('O')
        durationMs = [Math]::Round(([DateTimeOffset]::UtcNow - $started).TotalMilliseconds)
        results = @($results.ToArray()); error = $failure
    }
    $summaryPath = Join-Path $run 'summary.json'
    [IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 12))
    Write-Host "QA $Suite complete=$complete; evidence: $summaryPath"
    if ($env:GITHUB_STEP_SUMMARY) {
        $lines = @("## Lumi $Suite QA", '', "Complete: **$complete**", '', '| Suite | Passed |', '|---|---|')
        foreach ($result in $results) { $lines += "| $($result.name) | $($result.passed) |" }
        $lines -join "`n" | Out-File -LiteralPath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
    }
}
if (-not $complete) { exit 1 }
