#requires -Version 7.2
<#
.SYNOPSIS
Fast memory screening by default; explicitly Record/Compare deep calibrated regressions.
.DESCRIPTION
Regression status is separate from the absolute 500 MiB goal. Baselines are never automatically
overwritten. Exit 0 = recorded / no new regression, 3 = regression, 1 = incomplete/incomparable.
#>
[CmdletBinding()]
param(
    [ValidateSet('Fast','Record','Compare')][string]$Mode = 'Fast',
    [string]$BaselinePath,
    [string]$ProfilePath,
    [string]$SnapshotPath,
    [ValidateRange(2,100)][int]$Chats = 12,
    [ValidateRange(3,5)][int]$Rounds = 3,
    [ValidateRange(5,120)][int]$SampleSeconds = 10,
    [ValidateRange(0,1024)][int]$InjectRetainedMiB = 0,
    [string]$OutputRoot,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3
if (-not $IsWindows) { throw 'This headed private-resident memory pipeline currently supports Windows.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $BaselinePath) { $BaselinePath = Join-Path $repo 'diagnostics\memory\baseline.json' }
$BaselinePath = [IO.Path]::GetFullPath($BaselinePath)
if ($Mode -eq 'Fast') {
    if ($PSBoundParameters.ContainsKey('Rounds')) { throw '-Rounds belongs to Record/Compare; Fast has one full coverage pass.' }
    if (-not $SnapshotPath -and -not $ProfilePath -and (Test-Path -LiteralPath $BaselinePath)) {
        $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
        $SnapshotPath = $baseline.SnapshotDirectory
        if (-not $PSBoundParameters.ContainsKey('Chats')) { $Chats = $baseline.Runs[0].Options.Chats }
    }
    Write-Host 'Fast memory health screening, not a statistical baseline verdict. Use -Mode Compare for the deep comparison.'
    $arguments = @('-NoLogo','-NoProfile','-File',(Join-Path $PSScriptRoot 'Test-LumiMemory.ps1'),
        '-Mode','Fast','-Chats',"$Chats",'-SampleSeconds',"$SampleSeconds",'-InjectRetainedMiB',"$InjectRetainedMiB")
    if ($SnapshotPath) { $arguments += @('-SnapshotPath',$SnapshotPath) }
    if ($ProfilePath) { $arguments += @('-ProfilePath',$ProfilePath) }
    if ($OutputRoot) { $arguments += @('-OutputRoot',$OutputRoot) }
    if ($NoBuild) { $arguments += '-NoBuild' }
    & pwsh @arguments
    exit $LASTEXITCODE
}
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'diagnostics\memory' }
$output = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) (
    'pipeline-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'),[guid]::NewGuid().ToString('N').Substring(0,8))
[void][IO.Directory]::CreateDirectory($output)
$status = 1
try {
    if ($Mode -eq 'Record') {
        if (Test-Path -LiteralPath $BaselinePath) { throw 'The baseline already exists; choose a new path. Existing baselines are never ratcheted upward.' }
        if ($InjectRetainedMiB -gt 0) { throw 'Do not record a deliberately regressed diagnostic control as a baseline.' }
    }
    else {
        if ($ProfilePath -or $SnapshotPath) { throw 'Compare uses the saved immutable baseline snapshot, not a newly copied live profile.' }
        $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
        $SnapshotPath = $baseline.SnapshotDirectory
        $saved = $baseline.Runs[0].Options
        foreach ($name in @('Chats','Rounds','SampleSeconds')) {
            if ($PSBoundParameters.ContainsKey($name) -and (Get-Variable -Name $name -ValueOnly) -ne $saved.$name) {
                throw "$name does not match the saved workload."
            }
            Set-Variable -Name $name -Value $saved.$name
        }
    }
    $prepareRoot = Join-Path $output 'preparation'
    $prepareArgs = @('-NoLogo','-NoProfile','-File',(Join-Path $PSScriptRoot 'Test-LumiMemory.ps1'),
        '-Mode','Deep','-PrepareOnly','-OutputRoot',$prepareRoot)
    if ($NoBuild) { $prepareArgs += '-NoBuild' }
    if ($ProfilePath) { $prepareArgs += @('-ProfilePath',$ProfilePath) }
    if ($SnapshotPath) { $prepareArgs += @('-SnapshotPath',$SnapshotPath) }
    & pwsh @prepareArgs *> (Join-Path $output 'preparation.log')
    if ($LASTEXITCODE -ne 0) { throw "Preparation/build failed; see $output\preparation.log." }
    $prepared = @(Get-ChildItem -LiteralPath $prepareRoot -Directory)
    if ($prepared.Count -ne 1) { throw 'Preparation did not produce exactly one fresh profile.' }
    $frozen = Join-Path $prepared[0].FullName 'profile\Lumi'
    $project = Join-Path $repo 'src\Lumi\Lumi.csproj'
    $artifacts = Join-Path $repo '.mcp-run\memory-check'
    $target = & dotnet msbuild $project -nologo -getProperty:TargetPath `
        -property:Configuration=Release -property:LumiMemoryCheck=true `
        "-property:ArtifactsPath=$artifacts" -property:UseArtifactsOutput=true | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the regression evaluator.' }
    $currentExecutable = [IO.Path]::ChangeExtension($target.Trim(),'.exe')
    if ($Mode -eq 'Record') {
        $referenceDirectory = Join-Path $output 'reference'
        [void][IO.Directory]::CreateDirectory($referenceDirectory)
        foreach ($item in Get-ChildItem -LiteralPath (Split-Path -Parent $currentExecutable) -Force) {
            Copy-Item -LiteralPath $item.FullName -Destination $referenceDirectory -Recurse
        }
        $referenceExecutable = Join-Path $referenceDirectory ([IO.Path]::GetFileName($currentExecutable))
        $referenceFiles = @(Get-ChildItem -LiteralPath $referenceDirectory -Recurse -File | ForEach-Object {
            [ordered]@{ Path=[IO.Path]::GetRelativePath($referenceDirectory,$_.FullName); Hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
        [IO.File]::WriteAllText((Join-Path $output 'reference-files.json'),($referenceFiles | ConvertTo-Json -Depth 4))
    }
    else {
        $referenceExecutable = $baseline.ReferenceExecutable
        $referenceDirectory = Split-Path -Parent $referenceExecutable
        $referenceFiles = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $referenceDirectory) 'reference-files.json') -Raw | ConvertFrom-Json
        foreach ($file in $referenceFiles) {
            if ((Get-FileHash -LiteralPath (Join-Path $referenceDirectory $file.Path) -Algorithm SHA256).Hash -cne $file.Hash) {
                throw "The saved reference dependency changed: $($file.Path)."
            }
        }
    }
    $reports = @()
    $referenceReports = @()
    for ($run = 1; $run -le 5; $run++) {
        $order = if ($Mode -eq 'Record') { @('candidate') } elseif ($run % 2 -eq 1) { @('reference','candidate') } else { @('candidate','reference') }
        foreach ($side in $order) {
            Write-Host "[$Mode] $side fresh process $run/5; frozen input $frozen"
            $runRoot = Join-Path $output "$side-$run"
            $arguments = @('-NoLogo','-NoProfile','-File',(Join-Path $PSScriptRoot 'Test-LumiMemory.ps1'),
                '-Mode','Deep','-NoBuild','-SnapshotPath',$frozen,'-Chats',"$Chats",'-Rounds',"$Rounds",'-SampleSeconds',"$SampleSeconds",
                '-OutputRoot',$runRoot)
            if ($side -eq 'reference') { $arguments += @('-ReferenceExecutable',$referenceExecutable,'-ExpectedApplicationSha256',$baseline.Runs[0].ApplicationSha256) }
            else { $arguments += @('-InjectRetainedMiB',"$InjectRetainedMiB") }
            & pwsh @arguments *> (Join-Path $output "$side-$run.log")
            $code = $LASTEXITCODE
            if ($code -notin 0,3) { throw "$side run $run is incomplete; no regression verdict is valid." }
            $directories = @(Get-ChildItem -LiteralPath $runRoot -Directory)
            if ($directories.Count -ne 1) { throw "$side run $run did not produce one unique report." }
            $path = Join-Path $directories[0].FullName 'results.json'
            $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if (-not $report.Complete) { throw "$side run $run is incomplete." }
            Write-Host ("  mean {0:0.0} MiB; absolute budgets {1}" -f ($report.MeanRamBytes/1MB),$(if($report.Passed){'PASS'}else{'FAIL'}))
            if ($side -eq 'reference') { $referenceReports += $path } else { $reports += $path }
        }
    }
    $request = Join-Path $output 'evaluation-request.json'
    [IO.File]::WriteAllText($request,([ordered]@{
        Mode = $Mode.ToLowerInvariant(); BaselinePath = $BaselinePath; SnapshotDirectory = $frozen
        ReportFiles = $reports; OutputDirectory = $output
        ReferenceExecutable = $referenceExecutable; ReferenceReportFiles = $referenceReports
    } | ConvertTo-Json -Depth 4))
    $evaluator = [Diagnostics.Process]::new()
    try {
        $evaluator.StartInfo = [Diagnostics.ProcessStartInfo]::new([IO.Path]::ChangeExtension($target.Trim(),'.exe'))
        $evaluator.StartInfo.UseShellExecute = $false
        $evaluator.StartInfo.RedirectStandardOutput = $true
        $evaluator.StartInfo.RedirectStandardError = $true
        foreach ($arg in @('--memory-check','--memory-check-evaluate',$request)) { $evaluator.StartInfo.ArgumentList.Add($arg) }
        if (-not $evaluator.Start()) { throw 'Cannot start the regression evaluator.' }
        $stdout = $evaluator.StandardOutput.ReadToEndAsync()
        $stderr = $evaluator.StandardError.ReadToEndAsync()
        if (-not $evaluator.WaitForExit(60000)) { $evaluator.Kill($true);$evaluator.WaitForExit();throw 'Regression evaluation timed out.' }
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $output 'evaluation.log'),$text)
        Write-Host $text
        $status = $evaluator.ExitCode
        $verdict = Get-Content -LiteralPath (Join-Path $output 'regression.json') -Raw | ConvertFrom-Json
        if ($verdict.ExitCode -ne $status -or $status -notin 0,1,3) { throw 'Evaluator exit and persisted verdict disagree.' }
    }
    finally { $evaluator.Dispose() }
}
catch {
    $status = 1
    Write-Warning $_.ToString()
    [IO.File]::WriteAllText((Join-Path $output 'pipeline-error.txt'),$_.ToString())
}
Write-Host "Regression pipeline exit=$status. Evidence: $output"
exit $status
