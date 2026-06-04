[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$manifest = Join-Path $repoRoot 'dotnet-tools.json'

if (-not (Test-Path $manifest)) {
    throw "Could not find dotnet tool manifest at '$manifest'."
}

Write-Host "Restoring local .NET diagnostics tools from $manifest"
& dotnet tool restore --tool-manifest $manifest
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool restore failed with exit code $LASTEXITCODE."
}

Write-Host ""
& dotnet tool list --local
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool list failed with exit code $LASTEXITCODE."
}
