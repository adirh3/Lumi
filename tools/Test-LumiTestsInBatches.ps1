[CmdletBinding()]
param(
    [string]$Project = "tests/Lumi.Tests/Lumi.Tests.csproj",
    [string]$Configuration = "Release",
    [string]$ResultsDirectory = "TestResults",
    [ValidateRange(1, 100)]
    [int]$BatchSize = 20
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force $ResultsDirectory | Out-Null

$listArguments = @(
    "test",
    $Project,
    "--configuration", $Configuration,
    "--no-build",
    "--nologo",
    "--list-tests"
)
$listOutput = & dotnet @listArguments 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Could not enumerate Lumi desktop tests."
}

$testNames = $listOutput |
    ForEach-Object { $_.ToString().Trim() } |
    Where-Object { $_ -match "^Lumi\.Tests\." }
$classes = $testNames |
    ForEach-Object {
        if ($_ -match "^(Lumi\.Tests\.[^.]+)\.") {
            $matches[1]
        }
    } |
    Sort-Object -Unique

if ($classes.Count -eq 0) {
    throw "No Lumi desktop test classes were discovered."
}

$isolatedClasses = @(
    "Lumi.Tests.AnimationLifecycleRegressionTests",
    "Lumi.Tests.ChatViewScrollBehaviorTests",
    "Lumi.Tests.SearchOverlayLayoutTests"
) | Where-Object { $classes -contains $_ }
$batchedClasses = @($classes | Where-Object { $_ -notin $isolatedClasses })
$batchCount = [Math]::Ceiling($batchedClasses.Count / $BatchSize)

function Invoke-TestBatch {
    param(
        [string[]]$Classes,
        [string]$Label,
        [string]$ResultFile
    )

    $filter = ($Classes | ForEach-Object { "FullyQualifiedName~$_." }) -join "|"
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $attemptResultFile = if ($attempt -eq 1) {
            $ResultFile
        } else {
            "$([IO.Path]::GetFileNameWithoutExtension($ResultFile))-retry.trx"
        }
        Write-Host "Running $Label ($($Classes.Count) classes), attempt $attempt/2."
        $testArguments = @(
            "test",
            $Project,
            "--configuration", $Configuration,
            "--no-build",
            "--nologo",
            "--filter", $filter,
            "--blame-hang",
            "--blame-hang-timeout", "3m",
            "--blame-hang-dump-type", "none",
            "--logger", "trx;LogFileName=$attemptResultFile",
            "--results-directory", $ResultsDirectory
        )
        $output = & dotnet @testArguments 2>&1
        $exitCode = $LASTEXITCODE
        $output | ForEach-Object { Write-Output $_ }
        if ($exitCode -eq 0) {
            Start-Sleep -Seconds 2
            return
        }

        $sharedCopilotStartupFailed = ($output -join "`n").Contains(
            "The shared test Copilot service did not finish initializing.",
            [StringComparison]::Ordinal)
        if (-not $sharedCopilotStartupFailed -or $attempt -eq 2) {
            throw "$Label failed."
        }

        Write-Warning "$Label hit the shared Copilot startup transient; retrying in a fresh host."
        Start-Sleep -Seconds 5
    }
}

for ($index = 0; $index -lt $batchedClasses.Count; $index += $BatchSize) {
    $batchNumber = [int]($index / $BatchSize) + 1
    $end = [Math]::Min($index + $BatchSize - 1, $batchedClasses.Count - 1)
    $batch = $batchedClasses[$index..$end]
    Invoke-TestBatch `
        -Classes $batch `
        -Label "Lumi desktop test batch $batchNumber/$batchCount" `
        -ResultFile "lumi-desktop-batch-$batchNumber.trx"
}

foreach ($class in $isolatedClasses) {
    $shortName = $class.Substring("Lumi.Tests.".Length)
    Invoke-TestBatch `
        -Classes @($class) `
        -Label "isolated Lumi desktop test class $shortName" `
        -ResultFile "lumi-desktop-isolated-$shortName.trx"
}

Write-Host "All $($classes.Count) Lumi desktop test classes passed in $($batchCount + $isolatedClasses.Count) fresh hosts."
