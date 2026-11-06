<#
.SYNOPSIS
    Adds a release's What's new strings to all ten desktop string catalogs in one step.

.DESCRIPTION
    Reads a JSON file that gives each new key its text in every language:

        {
          "WhatsNew_Voice_Title": { "en": "Talk to Lumi", "he": "...", "es": "...", ... },
          "WhatsNew_Voice_Body":  { "en": "...", "he": "...", ... }
        }

    Every key must start with WhatsNew_, must be new, and needs text for every catalog in
    src/Lumi/Resources/Strings (the language is the catalog's file name: en, he, pt-BR, zh-Hans, ...). The keys go in,
    in the file's order, after the last WhatsNew_ key of each catalog, which keeps its encoding (UTF-8 without BOM) and
    line endings. Nothing is written unless every key passes. To change a string that exists, edit the catalogs.

.EXAMPLE
    pwsh -File '.github/skills/Update Whats New/Add-WhatsNewStrings.ps1' -Path "$env:TEMP/whats-new-1.0.3.json"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Path
)

$ErrorActionPreference = 'Stop'

$catalogDirectory = Resolve-Path (Join-Path $PSScriptRoot '..\..\..\src\Lumi\Resources\Strings')
$catalogs = @(Get-ChildItem -LiteralPath $catalogDirectory -Filter '*.json' | Sort-Object Name)
$languages = @($catalogs | ForEach-Object BaseName)

$strings = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
$entries = @($strings.PSObject.Properties)
if ($entries.Count -eq 0) {
    throw "$Path has no strings."
}

$problems = [System.Collections.Generic.List[string]]::new()
$texts = @{}
foreach ($catalog in $catalogs) {
    $texts[$catalog.FullName] = [System.IO.File]::ReadAllText($catalog.FullName)
}

foreach ($entry in $entries) {
    $key = $entry.Name
    if ($key -cnotmatch '^WhatsNew_[A-Za-z0-9]+(_[A-Za-z0-9]+)*$') {
        $problems.Add("$key isn't a What's new key; they read WhatsNew_<Feature>_Title, _Body, _Action.")
        continue
    }

    $translations = $entry.Value
    if ($translations -isnot [System.Management.Automation.PSCustomObject]) {
        $problems.Add("$key needs an object of texts by language.")
        continue
    }

    $missing = @($languages | Where-Object { [string]::IsNullOrWhiteSpace($translations.$_) })
    if ($missing.Count -gt 0) {
        $problems.Add("$key has no text for $($missing -join ', ').")
    }

    $unknown = @($translations.PSObject.Properties.Name | Where-Object { $_ -cnotin $languages })
    if ($unknown.Count -gt 0) {
        $problems.Add("$key has text for $($unknown -join ', '), but the catalogs are $($languages -join ', ').")
    }

    $existing = @($catalogs | Where-Object { $texts[$_.FullName] -match ('"' + [regex]::Escape($key) + '"\s*:') } | ForEach-Object Name)
    if ($existing.Count -gt 0) {
        $problems.Add("$key is already in $($existing -join ', ').")
    }
}

$updated = @{}
foreach ($catalog in $catalogs) {
    $text = $texts[$catalog.FullName]
    $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = [System.Collections.Generic.List[string]]::new([string[]]($text -split '\r?\n'))
    $anchor = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^\s*"WhatsNew_[^"]*"\s*:') {
            $anchor = $index
        }
    }

    if ($anchor -lt 0) {
        $problems.Add("$($catalog.Name) has no WhatsNew_ keys to add after.")
        continue
    }

    $indent = [regex]::Match($lines[$anchor], '^\s*').Value
    $followed = $lines[$anchor].TrimEnd().EndsWith(',')
    if (-not $followed) {
        $lines[$anchor] = $lines[$anchor].TrimEnd() + ','
    }

    $added = for ($index = 0; $index -lt $entries.Count; $index++) {
        $entry = $entries[$index]
        $value = ConvertTo-Json -InputObject ([string]$entry.Value.($catalog.BaseName)) -Compress
        $comma = if ($followed -or $index -lt $entries.Count - 1) { ',' } else { '' }
        "$indent`"$($entry.Name)`": $value$comma"
    }

    $lines.InsertRange($anchor + 1, [string[]]$added)
    $updated[$catalog.FullName] = $lines -join $newline
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { [Console]::Error.WriteLine("- $_") }
    [Console]::Error.WriteLine('Nothing was written.')
    exit 1
}

$encoding = [System.Text.UTF8Encoding]::new($false)
foreach ($catalog in $catalogs) {
    [System.IO.File]::WriteAllText($catalog.FullName, $updated[$catalog.FullName], $encoding)
}

Write-Host "Added to all $($catalogs.Count) catalogs: $(($entries | ForEach-Object Name) -join ', ')."
