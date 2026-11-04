#requires -Version 7.2
<#
.SYNOPSIS
Fails unless a commit has a successful PR Build that ran the full Windows desktop and mobile suites.
.DESCRIPTION
PR and manual PR Build runs skip the full Windows suites unless started with full_suite=true, and the
nightly runs them on whatever main was at night, so a green run doesn't by itself mean a commit passed
them. This finds a successful PR Build run for the exact commit in which every Windows job ran both
full suites and passed them. Needs the gh CLI with read access to Actions.
.EXAMPLE
pwsh tools/Assert-LumiFullSuite.ps1 -Sha (git rev-parse HEAD)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sha,
    [string]$Repository = 'adirh3/Lumi'
)

$ErrorActionPreference = 'Stop'
$fullSuiteSteps = @(
    'Test (desktop Windows suite in fresh-host batches)',
    'Test (mobile Windows suite)'
)

function Test-RanFullSuites {
    param($Job)

    foreach ($name in $fullSuiteSteps) {
        if (-not ($Job.steps | Where-Object { $_.name -eq $name -and $_.conclusion -eq 'success' })) {
            return $false
        }
    }
    return $true
}

$runs = gh api "repos/$Repository/actions/workflows/pr-validation.yml/runs?head_sha=$Sha&per_page=50" |
    ConvertFrom-Json
if ($LASTEXITCODE -ne 0) {
    throw "Could not list PR Build runs for $Sha."
}

foreach ($run in $runs.workflow_runs | Where-Object { $_.conclusion -eq 'success' }) {
    $jobs = (gh api "repos/$Repository/actions/runs/$($run.id)/jobs?per_page=100" | ConvertFrom-Json).jobs
    $windowsJobs = @($jobs | Where-Object { $_.name -like 'Validate (windows-*' })
    $incomplete = @($windowsJobs | Where-Object { -not (Test-RanFullSuites $_) })
    if ($windowsJobs.Count -gt 0 -and $incomplete.Count -eq 0) {
        Write-Host "PR Build $($run.html_url) passed the full Windows suites for $Sha on $($windowsJobs.Count) Windows runners."
        return
    }
}

throw ("No successful PR Build ran the full Windows suites for $Sha. Run " +
    "'gh workflow run pr-validation.yml --ref <branch> -f full_suite=true' for this commit and wait for it.")
