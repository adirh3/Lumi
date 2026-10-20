#requires -Version 7.2
<#
.SYNOPSIS
Aggregates local benchmark results without discarding failures.
.DESCRIPTION
ResultPath accepts results.json files or directories containing them. The first
input's label is the baseline. Duplicate file paths are read once. Metrics are
grouped by label, scenario, and immutable prompt/parameter/fixture/runner hash.

All planned cases, including skipped and unattempted cases, count toward the
success rate. Medians are explicitly successful-run-only. Speedup is emitted
only for matching contracts and sample counts with every run passing and every
timing available. Missing durations are null, never zero. Known totals include
failed runs with measurements and are separate from complete totals.
Output availability is reported separately; unavailable output never becomes
zero characters or prevents an otherwise valid timing comparison.

Prints JSON, optionally writes the same JSON to a NEW OutputPath. No GUI work,
bridge requests, or access to Lumi chat storage is performed.
.EXAMPLE
.\Compare-UIAutomationBenchmarks.ps1 -ResultPath C:\Temp\LumiUiBench\baseline-*\results.json,C:\Temp\LumiUiBench\improved-*\results.json
.EXAMPLE
.\Compare-UIAutomationBenchmarks.ps1 -ResultPath C:\Temp\LumiUiBench -OutputPath C:\Temp\comparison.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]]$ResultPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

function Get-Median {
    param([object[]]$Values)
    $numbers = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    if ($numbers.Count -eq 0) { return $null }
    $middle = [int][Math]::Floor($numbers.Count / 2)
    if ($numbers.Count % 2) { return [Math]::Round($numbers[$middle], 2) }
    return [Math]::Round(($numbers[$middle - 1] + $numbers[$middle]) / 2, 2)
}

function Get-KnownSum {
    param([object[]]$Values)
    $sum = 0.0
    foreach ($value in $Values) {
        if ($null -ne $value) { $sum += [double]$value }
    }
    return [Math]::Round($sum, 2)
}

$paths = [Collections.Generic.List[string]]::new()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($inputPath in $ResultPath) {
    $items = @(Get-Item -Path $inputPath -ErrorAction Stop)
    foreach ($item in $items) {
        $files = if ($item.PSIsContainer) {
            @(Get-ChildItem -LiteralPath $item.FullName -Filter 'results.json' -File -Recurse | Sort-Object FullName)
        }
        else { @($item) }
        foreach ($file in $files) {
            if ($seen.Add($file.FullName)) { $paths.Add($file.FullName) }
        }
    }
}
if ($paths.Count -eq 0) { throw 'No results.json artifacts found.' }

$records = [Collections.Generic.List[object]]::new()
$baselineLabel = $null
foreach ($path in $paths) {
    $bundle = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable -Depth 100
    if ($bundle['schemaVersion'] -ne 1 -or -not $bundle.Contains('results') -or
        @($bundle['results']).Count -eq 0) {
        throw "Not a supported nonempty benchmark bundle: $path"
    }
    if ($null -eq $baselineLabel) { $baselineLabel = [string]$bundle['label'] }
    foreach ($run in $bundle['results']) {
        $records.Add([pscustomobject]@{
            label = [string]$bundle['label']; scenario = [string]$run['scenario']
            contract = [string]$run['comparisonKey']; sourcePath = $path; run = $run
        })
    }
}

$rows = @(
    foreach ($group in ($records | Group-Object label, scenario, contract)) {
        $first = $group.Group[0]
        $runs = @($group.Group | ForEach-Object { $_.run })
        # Require both the success flag and terminal status; interrupted runs are never successes.
        $successes = @($runs | Where-Object { $_['success'] -eq $true -and $_['status'] -eq 'passed' })
        $timed = @($runs | Where-Object {
            $null -ne $_['totalToolDurationMs'] -and $null -ne $_['elapsedMs'] -and
            $null -ne $_['toolCallCount'] -and $_['transcriptComplete'] -eq $true
        })
        $knownRuntime = Get-KnownSum @($runs | ForEach-Object { $_['knownToolDurationMs'] })
        $runsWithOutput = @($runs | Where-Object { $null -ne $_['knownToolOutputChars'] })
        $completeOutputRuns = @($runs | Where-Object { $_['toolOutputsComplete'] -eq $true })
        $knownOutputChars = if ($runsWithOutput.Count -gt 0) {
            Get-KnownSum @($runsWithOutput | ForEach-Object { $_['knownToolOutputChars'] })
        } else { $null }
        [pscustomobject][ordered]@{
            label = $first.label; scenario = $first.scenario; comparisonKey = $first.contract
            model = $runs[0]['model']; reasoningEffort = $runs[0]['reasoningEffort']
            planned = $runs.Count; passed = $successes.Count
            failed = @($runs | Where-Object { $_['status'] -eq 'failed' }).Count
            skipped = @($runs | Where-Object { $_['status'] -eq 'skipped' }).Count
            notRun = @($runs | Where-Object { $_['status'] -notin @('passed', 'failed', 'skipped') }).Count
            assertionPassed = @($runs | Where-Object { $_['assertionPassed'] -eq $true }).Count
            successRatePercent = [Math]::Round(100.0 * $successes.Count / $runs.Count, 2)
            fullyMeasuredRuns = $timed.Count
            medianElapsedMsSuccessfulOnly = Get-Median @($successes | ForEach-Object { $_['elapsedMs'] })
            medianToolCallsSuccessfulOnly = Get-Median @($successes | ForEach-Object { $_['toolCallCount'] })
            medianToolDurationMsSuccessfulOnly = Get-Median @($successes | ForEach-Object { $_['totalToolDurationMs'] })
            knownToolCallsAllRuns = Get-KnownSum @($runs | ForEach-Object { $_['toolCallCount'] })
            knownToolDurationMsAllRuns = $knownRuntime
            totalToolDurationMsAllRuns = if ($timed.Count -eq $runs.Count) { $knownRuntime } else { $null }
            runsWithCompleteToolOutput = $completeOutputRuns.Count
            availableToolOutputs = Get-KnownSum @($runs | ForEach-Object { $_['availableToolOutputCount'] })
            unavailableToolOutputs = Get-KnownSum @($runs | ForEach-Object { $_['missingToolOutputCount'] })
            knownToolOutputCharsAllRuns = $knownOutputChars
            totalToolOutputCharsAllRuns = if ($completeOutputRuns.Count -eq $runs.Count) {
                Get-KnownSum @($runs | ForEach-Object { $_['toolOutputChars'] })
            } else { $null }
            sources = @($group.Group.sourcePath | Sort-Object -Unique)
        }
    }
)
$comparisons = @(
    foreach ($candidate in @($rows | Where-Object { $_.label -ne $baselineLabel })) {
        $baselines = @($rows | Where-Object { $_.label -eq $baselineLabel -and $_.scenario -eq $candidate.scenario })
        $matching = @($baselines | Where-Object { $_.comparisonKey -ceq $candidate.comparisonKey })
        $reason = $null
        $speedup = $null
        $runtimeSpeedup = $null
        if ($matching.Count -ne 1) { $reason = 'No unique baseline with the identical prompt/parameters/fixture/runner contract.' }
        else {
            $baseline = $matching[0]
            if ($baseline.planned -ne $candidate.planned) { $reason = 'Different planned sample counts.' }
            elseif ($baseline.passed -ne $baseline.planned -or $candidate.passed -ne $candidate.planned) {
                $reason = 'A side contains failed, skipped, or unattempted runs; no speedup claim.'
            }
            elseif ($baseline.fullyMeasuredRuns -ne $baseline.planned -or $candidate.fullyMeasuredRuns -ne $candidate.planned) {
                $reason = 'Timing/transcript measurements are incomplete.'
            }
            elseif ($candidate.medianElapsedMsSuccessfulOnly -le 0) { $reason = 'Candidate elapsed time is not positive.' }
            else {
                $speedup = [Math]::Round($baseline.medianElapsedMsSuccessfulOnly / $candidate.medianElapsedMsSuccessfulOnly, 3)
                if ($candidate.medianToolDurationMsSuccessfulOnly -gt 0) {
                    $runtimeSpeedup = [Math]::Round(
                        $baseline.medianToolDurationMsSuccessfulOnly / $candidate.medianToolDurationMsSuccessfulOnly, 3)
                }
            }
        }
        [ordered]@{
            scenario = $candidate.scenario; baselineLabel = $baselineLabel; candidateLabel = $candidate.label
            comparisonKey = $candidate.comparisonKey; comparableAllPass = $null -eq $reason
            medianElapsedSpeedup = $speedup; medianToolRuntimeSpeedup = $runtimeSpeedup; reason = $reason
        }
    }
)
$report = [ordered]@{
    schemaVersion = 1; generatedAt = [DateTimeOffset]::UtcNow.ToString('o'); baselineLabel = $baselineLabel
    denominatorPolicy = 'Every planned run counts. Speedups require matching, equally sized, fully measured, all-pass cohorts.'
    rows = $rows; comparisons = $comparisons; sourceFiles = $paths.ToArray()
}
$json = ConvertTo-Json -InputObject $report -Depth 30
if ($OutputPath) {
    $absolutePath = [IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $absolutePath) { throw 'OutputPath already exists; refusing to overwrite it.' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolutePath))
    [IO.File]::WriteAllText($absolutePath, $json, [Text.UTF8Encoding]::new($false))
}
Write-Output $json
