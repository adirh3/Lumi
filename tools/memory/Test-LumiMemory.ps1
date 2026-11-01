#requires -Version 7.2
<#
.SYNOPSIS
Checks desktop RAM, absolute bloat and released-view retention in an isolated optimized Lumi run.
.DESCRIPTION
Default Fast: a 2,000-chat SQLite fixture, 12 distinct chats, stored-history scrolling, every management
and Settings page once, and separate normal-use and post-GC measurements. Deep repeats the workload.
No inference is
sent. -ProfilePath takes an online SQLite backup, never runs against the original profile.
Exit 0 = complete and within budget; 3 = complete but over budget; 1 = incomplete/invalid run.
#>
[CmdletBinding()]
param(
    [ValidateSet('Fast','Deep')][string]$Mode = 'Fast',
    [ValidateSet('Strict','CI')][string]$Policy = 'Strict',
    [ValidateRange(2, 100)][int]$Chats = 12,
    [ValidateRange(3, 20)][int]$Rounds = 3,
    [ValidateRange(5, 120)][int]$SampleSeconds = 10,
    [ValidateRange(1, 16384)][int]$MaxRamMiB = 500,
    [ValidateRange(1, 32768)][int]$MaxPrivateMiB = 1024,
    [ValidateRange(1, 8192)][int]$MaxManagedMiB = 200,
    [string]$ProfilePath,
    [string]$SnapshotPath,
    [switch]$PrepareOnly,
    [ValidateRange(0,1024)][int]$InjectRetainedMiB = 0,
    [string]$ReferenceExecutable,
    [string]$ExpectedApplicationSha256,
    [string]$OutputRoot,
    [string]$ArtifactsPath,
    [switch]$NoBuild,
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
if (-not $IsWindows) { throw 'This private-resident RAM budget check currently supports Windows. Use the stress/history harnesses on other platforms.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repo 'src\Lumi\Lumi.csproj'
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'diagnostics\memory' }
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $repo '.mcp-run\memory-check' }
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
$run = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) (
    'check-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8))
[void][IO.Directory]::CreateDirectory($run)
$stamp = Join-Path $ArtifactsPath 'memory-check-build.json'

# A reused binary must match the current source, including uncommitted and untracked code.
$inputRoots = @('src\Lumi', 'src\Lumi.Generators', 'src\Lumi.Remote.Protocol', 'Strata\src', 'build\Copilot')
$inputs = foreach ($root in $inputRoots) {
    Get-ChildItem -LiteralPath (Join-Path $repo $root) -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.axaml', '.csproj', '.props', '.targets', '.json' -and
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
}
$inputs += Get-ChildItem -LiteralPath $repo -File | Where-Object { $_.Name -like 'Directory.*' -or $_.Name -eq 'global.json' }
$fingerprintText = ($inputs | Sort-Object FullName | ForEach-Object {
    '{0}:{1}' -f [IO.Path]::GetRelativePath($repo, $_.FullName), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}) -join "`n"
$fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($fingerprintText)))
$process = $null
$processStarted = $false
$report = $null
$exitCode = 1
$errorText = $null
try {
    if ($Policy -eq 'CI') {
        if ($Mode -ne 'Fast') { throw 'CI policy only supports Fast synthetic screening.' }
        foreach ($limit in @(@('MaxRamMiB',1024),@('MaxPrivateMiB',2048),@('MaxManagedMiB',200))) {
            if ($PSBoundParameters.ContainsKey($limit[0]) -and (Get-Variable -Name $limit[0] -ValueOnly) -ne $limit[1]) {
                throw 'CI uses fixed declared guardrails; do not silently alter them.'
            }
            Set-Variable -Name $limit[0] -Value $limit[1]
        }
    }
    if ($Mode -eq 'Fast' -and $PSBoundParameters.ContainsKey('Rounds')) { throw '-Rounds belongs to Deep mode; Fast executes one complete coverage pass.' }
    if ($ProfilePath -and $SnapshotPath) { throw 'Use -ProfilePath or -SnapshotPath, not both.' }
    if ($ReferenceExecutable) {
        $ReferenceExecutable = [IO.Path]::GetFullPath($ReferenceExecutable)
        if (-not $ExpectedApplicationSha256 -or $ExpectedApplicationSha256.Length -ne 64 `
            -or (Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($ReferenceExecutable,'.dll')) -Algorithm SHA256).Hash -cne $ExpectedApplicationSha256) {
            throw 'The explicit reference executable must match the saved calibrated application hash.'
        }
    }
    elseif ($NoBuild) {
        if (-not (Test-Path -LiteralPath $stamp)) { throw '-NoBuild requires a prior successful memory-check build.' }
        $prior = Get-Content -LiteralPath $stamp -Raw | ConvertFrom-Json
        if ($prior.fingerprint -cne $fingerprint) { throw 'The cached memory-check binary is stale. Run again without -NoBuild.' }
    }
    else {
        Write-Host 'Building optimized Lumi with opt-in memory diagnostics...'
        & dotnet build $project -c Release -p:LumiMemoryCheck=true --artifacts-path $ArtifactsPath --nologo -v:q *> (Join-Path $run 'build.log')
        if ($LASTEXITCODE -ne 0) { throw "Build failed; see $(Join-Path $run 'build.log')." }
        [IO.File]::WriteAllText($stamp, (@{ fingerprint = $fingerprint; configuration = 'Release'; diagnostics = $true } | ConvertTo-Json))
    }
    if ($ReferenceExecutable) { $executable = $ReferenceExecutable }
    else {
        $target = & dotnet msbuild $project -nologo -getProperty:TargetPath `
            -property:Configuration=Release -property:LumiMemoryCheck=true `
            "-property:ArtifactsPath=$ArtifactsPath" -property:UseArtifactsOutput=true | Select-Object -Last 1
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the optimized memory-check executable.' }
        $executable = [IO.Path]::ChangeExtension($target.Trim(), '.exe')
    }
    if (-not (Test-Path -LiteralPath $executable)) { throw "No built executable at $executable." }
    $profileRoot = Join-Path $run 'profile'
    $prepare = [Diagnostics.Process]::new()
    try {
        $prepare.StartInfo = [Diagnostics.ProcessStartInfo]::new($executable)
        $prepare.StartInfo.WorkingDirectory = $repo
        $prepare.StartInfo.UseShellExecute = $false
        $prepare.StartInfo.RedirectStandardOutput = $true
        $prepare.StartInfo.RedirectStandardError = $true
        $prepare.StartInfo.Environment['LUMI_APPDATA_DIR'] = $profileRoot
        $prepare.StartInfo.Environment['COPILOT_HOME'] = Join-Path $profileRoot 'Lumi\copilot-home'
        foreach ($argument in @('--memory-check','--memory-check-prepare-only','true')) { $prepare.StartInfo.ArgumentList.Add($argument) }
        if ($ProfilePath) {
            $prepare.StartInfo.ArgumentList.Add('--memory-check-profile')
            $prepare.StartInfo.ArgumentList.Add([IO.Path]::GetFullPath($ProfilePath))
        }
        if ($SnapshotPath) {
            $prepare.StartInfo.ArgumentList.Add('--memory-check-snapshot')
            $prepare.StartInfo.ArgumentList.Add([IO.Path]::GetFullPath($SnapshotPath))
        }
        if (-not $prepare.Start()) { throw 'Could not start profile preparation.' }
        $preparationOutput = $prepare.StandardOutput.ReadToEndAsync()
        $preparationErrors = $prepare.StandardError.ReadToEndAsync()
        if (-not $prepare.WaitForExit(180000)) { $prepare.Kill($true); $prepare.WaitForExit(); throw 'Profile preparation timed out.' }
        $preparationText = $preparationOutput.GetAwaiter().GetResult() + $preparationErrors.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $run 'preparation.log'), $preparationText)
        Write-Host $preparationText
        if ($prepare.ExitCode -ne 0) { throw 'Profile preparation failed; no measured app was launched.' }
    }
    finally { $prepare.Dispose() }
    $manifest = Get-Content -LiteralPath (Join-Path $profileRoot 'Lumi\memory-profile.json') -Raw | ConvertFrom-Json
    if ($manifest.Version -ne 1 -or $manifest.Fingerprint.Length -ne 64) { throw 'No valid frozen-profile identity was prepared.' }
    if ($PrepareOnly) {
        $exitCode = 0
        Write-Host "Profile PREPARED (no memory verdict): $(Join-Path $profileRoot 'Lumi')"
        return
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($executable)
    $process.StartInfo.WorkingDirectory = $repo
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.Environment['LUMI_APPDATA_DIR'] = $profileRoot
    $process.StartInfo.Environment['COPILOT_HOME'] = Join-Path $run 'profile\Lumi\copilot-home'
    $arguments = @('--memory-check', '--memory-check-chats', "$Chats", '--memory-check-rounds', "$Rounds",
        '--memory-check-sample-seconds', "$SampleSeconds", '--memory-check-max-ram-mib', "$MaxRamMiB",
        '--memory-check-max-private-mib', "$MaxPrivateMiB", '--memory-check-max-managed-mib', "$MaxManagedMiB",
        '--memory-check-prepared', $manifest.Fingerprint,
        '--memory-check-inject-retained-mib', "$InjectRetainedMiB",
        '--memory-check-output', $run)
    # Absence of this flag preserves the archived Deep executable's protocol.
    if ($Mode -eq 'Fast') { $arguments += @('--memory-check-fast','true') }
    if ($Policy -eq 'CI') { $arguments += @('--memory-check-ci','true') }
    if ($KeepOpen) { $arguments += '--memory-keep-open' }
    foreach ($argument in $arguments) { $process.StartInfo.ArgumentList.Add($argument) }
    if (-not $process.Start()) { throw 'Could not launch the memory check.' }
    $processStarted = $true
    $stdout = [Text.StringBuilder]::new()
    $stderr = $process.StandardError.ReadToEndAsync()
    [IO.File]::WriteAllText((Join-Path $run 'instance.json'), (@{
        pid = $process.Id; startedAt = $process.StartTime.ToString('O'); executable = $executable
        appDataRoot = $process.StartInfo.Environment['LUMI_APPDATA_DIR']
    } | ConvertTo-Json))
    Write-Host "$Mode memory check PID $($process.Id). Evidence: $run"
    if ($KeepOpen) { Write-Host 'The isolated window will remain open after its check; close it to finish this command.' }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $next = $process.StandardOutput.ReadLineAsync()
        while (-not $next.IsCompleted) {
            if (-not $KeepOpen -and $watch.Elapsed.TotalMinutes -gt 15) {
                $process.Kill($true) # Only the process tree created above; never an existing Lumi.
                $process.WaitForExit()
                throw 'The check exceeded fifteen minutes; no memory verdict is valid.'
            }
            Start-Sleep -Milliseconds 50
        }
        $line = $next.GetAwaiter().GetResult()
        if ($null -eq $line) { break }
        [void]$stdout.AppendLine($line)
        Write-Host $line
    }
    if (-not $process.WaitForExit(10000)) { throw 'The stdout stream ended without the check exiting.' }
    $text = $stdout.ToString()
    $errors = $stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $run 'stdout.log'), $text)
    [IO.File]::WriteAllText((Join-Path $run 'stderr.log'), $errors)
    if ($errors) { Write-Output $errors }
    $path = Join-Path $run 'results.json'
    if (-not (Test-Path -LiteralPath $path)) { throw "No memory report was produced (exit $($process.ExitCode))." }
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $measuredRounds = if ($Mode -eq 'Fast') { 1 } else { $Rounds }
    $expected = @(1..$measuredRounds | ForEach-Object { "chats-$_"; "after-pages-$_" } | Sort-Object -CaseSensitive)
    $actual = @($report.Phases | ForEach-Object Id | Sort-Object -CaseSensitive)
    $schema = if ($Mode -eq 'Fast') { 3 } else { 2 }
    $valid = $report.SchemaVersion -eq $schema -and $report.ProcessId -eq $process.Id `
        -and $report.ProfileFingerprint -ceq $manifest.Fingerprint `
        -and $report.RuntimeHomeIsolated -eq $true `
        -and $report.RuntimeHome -ceq $process.StartInfo.Environment['COPILOT_HOME'] `
        -and $report.CliSha256.Length -eq 64 `
        -and $report.Options.Chats -eq $Chats -and $report.Options.Rounds -eq $Rounds `
        -and $report.Options.SampleSeconds -eq $SampleSeconds `
        -and $report.Options.MaxRamBytes -eq $MaxRamMiB * 1MB `
        -and $report.Options.MaxPrivateBytes -eq $MaxPrivateMiB * 1MB `
        -and $report.Options.MaxManagedBytes -eq $MaxManagedMiB * 1MB `
        -and ($expected -join "`n") -ceq ($actual -join "`n") `
        -and $report.Build.StartsWith('Release with opt-in', [StringComparison]::Ordinal)
    if (-not $valid -or $report.Complete -ne $true) { throw 'The workload/report is incomplete or mismatched; do not treat it as a budget pass.' }
    if ($report.Passed -eq $true -and $process.ExitCode -eq 0 -and $text.Contains('[RESULT] LUMI_MEMORY_CHECK_OK', [StringComparison]::Ordinal)) {
        $exitCode = 0
    }
    elseif ($report.Passed -eq $false -and $process.ExitCode -eq 3 -and $report.BudgetFailures.Count -gt 0) {
        $exitCode = 3
    }
    else { throw 'The process exit and report verdict disagree.' }
}
catch {
    $errorText = $_.ToString()
    Write-Warning $errorText
}
finally {
    if ($null -ne $process) {
        if ($processStarted -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        $process.Dispose()
    }
    [IO.File]::WriteAllText((Join-Path $run 'summary.json'), ([ordered]@{
        complete = $null -eq $errorText; passed = -not $PrepareOnly -and $exitCode -eq 0; exitCode = $exitCode
        mode = $Mode
        policy = $Policy
        preparationOnly = [bool]$PrepareOnly
        sourceFingerprint = $(if($ReferenceExecutable){$null}else{$fingerprint}); results = 'results.json'; error = $errorText
        referenceExecutable = $ReferenceExecutable
        scope = 'Windows headed reading/navigation; Lumi and child private residency; no inference or browser/voice/active-agent workload'
    } | ConvertTo-Json -Depth 5))
    Write-Host "Memory check exit=$exitCode. Report: $(Join-Path $run 'summary.json')"
    if (Test-Path -LiteralPath (Join-Path $run 'report.txt')) { Write-Host "Readable assessment: $(Join-Path $run 'report.txt')" }
}
exit $exitCode
