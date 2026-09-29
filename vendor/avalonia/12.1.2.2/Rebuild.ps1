param(
    [string]$WorkRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$source = Join-Path $WorkRoot 'source'
$patches = @(
    (Join-Path $PSScriptRoot 'patches/0001-textbox-selection-notifications.patch'),
    (Join-Path $PSScriptRoot 'patches/0002-textlayout-empty-ranges.patch')
)
$commit = 'd3c867a9e2de379249b03dbeb3495bd7f076a81a'

function Assert-Exit([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed with exit code $LASTEXITCODE." }
}

New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
if (-not (Test-Path (Join-Path $source '.git'))) {
    gh repo clone AvaloniaUI/Avalonia $source -- --branch 12.1.2 --depth 1 `
        --config core.longpaths=true --config core.autocrlf=true
    Assert-Exit 'Source clone'
}
if ((git -C $source rev-parse HEAD).Trim() -cne $commit) {
    throw "Expected upstream commit $commit; refusing to modify a different checkout."
}
git -C $source config core.longpaths true
git -C $source config core.autocrlf true
git -C $source -c core.longpaths=true submodule update --init --depth 1 external/XamlX
Assert-Exit 'Declared XamlX checkout'

foreach ($patch in $patches) {
    git -C $source apply --reverse --check $patch
    if ($LASTEXITCODE -ne 0) {
        git -C $source apply --check $patch
        Assert-Exit 'Patch validation'
        git -C $source apply $patch
        Assert-Exit 'Source patch'
    }
}

$referenceDir = Join-Path $WorkRoot 'reference'
New-Item -ItemType Directory -Path $referenceDir -Force | Out-Null
$official = Join-Path $referenceDir 'Avalonia.12.1.2.nupkg'
if (-not (Test-Path $official)) {
    Invoke-WebRequest 'https://api.nuget.org/v3-flatcontainer/avalonia/12.1.2/avalonia.12.1.2.nupkg' -OutFile $official
}
if ((Get-FileHash $official -Algorithm SHA256).Hash -ine '99987414c63ac3993346a84a852006df963ff839f96d140557ee31f8850db08f') {
    throw 'Official input package SHA-256 mismatch.'
}

$exe = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'dotnet.exe' } else { 'dotnet' }
$dotnet = Join-Path $WorkRoot "dotnet/$exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_ROOT = Split-Path $dotnet
$env:DOTNET_CLI_HOME = Join-Path $WorkRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $WorkRoot '.nuget/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:PATH = "$(Split-Path $dotnet)$([IO.Path]::PathSeparator)$env:PATH"

Push-Location $source
try {
    $sdk = & $dotnet --version
    Assert-Exit 'Pinned SDK resolution (install SDK 10.0.201 locally if missing)'
    if ($sdk.Trim() -cne '10.0.201') {
        throw "For this artifact use exact SDK 10.0.201, not $sdk."
    }
    $properties = @(
        '-p:ContinuousIntegrationBuild=true',
        '-p:NuGetAudit=false',
        '-p:AssemblyVersion=12.1.2.0',
        '-p:FileVersion=12.1.2.2',
        '-p:InformationalVersion=12.1.2.2-lumi-text-contract-fixes',
        '-p:EmbedAllSources=true'
    )
    & $dotnet build src/Avalonia.Controls/Avalonia.Controls.csproj -c Release @properties
    Assert-Exit 'Release build for net8.0 and net10.0'
    & $dotnet test --project tests/Avalonia.Controls.UnitTests/Avalonia.Controls.UnitTests.csproj `
        -c Release --filter-class 'Avalonia.Controls.UnitTests.TextBox*' --report-trx `
        --results-directory (Join-Path $WorkRoot 'verification/combined-textbox') @properties
    Assert-Exit 'All TextBox tests'
    & $dotnet test --project tests/Avalonia.Skia.UnitTests/Avalonia.Skia.UnitTests.csproj `
        -c Release --filter-class 'Avalonia.Skia.UnitTests.Media.TextFormatting.TextLayout*' --report-trx `
        --results-directory (Join-Path $WorkRoot 'verification/combined-textlayout') @properties
    Assert-Exit 'All TextLayout tests'
}
finally {
    Pop-Location
}

& (Join-Path $PSScriptRoot 'tools/Verify-Compatibility.ps1') -WorkRoot $WorkRoot
python (Join-Path $PSScriptRoot 'tools/pack.py') --root $WorkRoot --inputs $PSScriptRoot
Assert-Exit 'Offline package construction'
