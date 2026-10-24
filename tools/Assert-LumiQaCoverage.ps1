#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TrxPath,
    [Parameter(Mandatory)][string]$CatalogPath,
    [Parameter(Mandatory)][ValidateSet('desktop', 'mobile')][string]$Project
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$catalog = Get-Content -LiteralPath $CatalogPath -Raw | ConvertFrom-Json
if ($catalog.schemaVersion -ne 1) { throw 'Unsupported CoreQA coverage catalog.' }
$required = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($method in @($catalog.$Project)) {
    if ([string]::IsNullOrWhiteSpace($method) -or -not $required.Add($method)) {
        throw "Empty or duplicate CoreQA catalog entry: '$method'."
    }
}
if ($required.Count -eq 0) { throw "The $Project CoreQA catalog is empty." }

[xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
$ns = [Xml.XmlNamespaceManager]::new($trx.NameTable)
$ns.AddNamespace('t', $trx.DocumentElement.NamespaceURI)
$definitions = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($definition in $trx.SelectNodes('/t:TestRun/t:TestDefinitions/t:UnitTest', $ns)) {
    $method = $definition.SelectSingleNode('t:TestMethod', $ns)
    if ($null -eq $method) { throw 'A CoreQA test definition has no method metadata.' }
    $class = $method.GetAttribute('className').Split(',')[0].Trim()
    $name = $method.GetAttribute('name')
    if (-not $class -or -not $name) { throw 'A CoreQA test definition has incomplete method metadata.' }
    $definitions.Add($definition.GetAttribute('id'), "$class.$name")
}
$executed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$cases = 0
foreach ($result in $trx.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns)) {
    if ($result.GetAttribute('outcome') -cne 'Passed') {
        throw "CoreQA case did not pass: $($result.GetAttribute('testName'))."
    }
    $method = ''
    if (-not $definitions.TryGetValue($result.GetAttribute('testId'), [ref]$method)) {
        throw 'A CoreQA result has no matching test definition.'
    }
    [void]$executed.Add($method)
    $cases++
}
if ($cases -eq 0) { throw 'No CoreQA cases were executed.' }
$missing = @($required | Where-Object { -not $executed.Contains($_) } | Sort-Object)
$unexpected = @($executed | Where-Object { -not $required.Contains($_) } | Sort-Object)
if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
    throw ("CoreQA membership changed. Missing: [{0}]. Unregistered: [{1}]. Review tools\qa-core-coverage.json together with the CoreQA traits." -f
        ($missing -join ', '), ($unexpected -join ', '))
}
[ordered]@{
    complete = $true; requiredMethods = $required.Count
    passedMethods = $executed.Count; passedCases = $cases
} | ConvertTo-Json -Compress
