param(
    [Parameter(Mandatory = $true)][string]$WorkRoot
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $WorkRoot 'source'
$cecil = Join-Path $WorkRoot '.nuget/packages/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll'
if (-not (Test-Path $cecil)) {
    throw 'Run the upstream manifested build first; its Mono.Cecil dependency is required for metadata verification.'
}
[Reflection.Assembly]::LoadFrom($cecil) | Out-Null

function Get-Definitions($assembly) {
    $rows = [Collections.Generic.List[string]]::new()
    foreach ($reference in $assembly.MainModule.AssemblyReferences) {
        $rows.Add("REFERENCE|$($reference.FullName)")
    }
    foreach ($type in $assembly.MainModule.GetTypes()) {
        $rows.Add("TYPE|$($type.FullName)|$($type.Attributes)|$($type.BaseType.FullName)")
        foreach ($interface in $type.Interfaces) {
            $rows.Add("INTERFACE|$($type.FullName)|$($interface.InterfaceType.FullName)")
        }
        foreach ($field in $type.Fields) {
            $rows.Add("FIELD|$($field.FullName)|$($field.Attributes)|$($field.Constant)")
        }
        foreach ($method in $type.Methods) {
            $rows.Add("METHOD|$($method.FullName)|$($method.Attributes)|$($method.ImplAttributes)")
            foreach ($parameter in $method.Parameters) {
                $rows.Add("PARAMETER|$($method.FullName)|$($parameter.Index)|$($parameter.Name)|$($parameter.Attributes)|$($parameter.Constant)")
            }
            foreach ($parameter in $method.GenericParameters) {
                $rows.Add("GENERIC|$($method.FullName)|$($parameter.Name)|$($parameter.Attributes)")
                foreach ($constraint in $parameter.Constraints) {
                    $rows.Add("CONSTRAINT|$($method.FullName)|$($parameter.Name)|$($constraint.ConstraintType.FullName)")
                }
            }
        }
        foreach ($property in $type.Properties) {
            $rows.Add("PROPERTY|$($property.FullName)|$($property.Attributes)")
        }
        foreach ($event in $type.Events) {
            $rows.Add("EVENT|$($event.FullName)|$($event.Attributes)")
        }
    }
    $rows.Sort([StringComparer]::Ordinal)
    return $rows.ToArray()
}

function Get-CallCount($method, [string]$pattern) {
    return @($method.Body.Instructions |
        Where-Object { "$($_.Operand)" -like $pattern }).Count
}

function Assert-Batching($originalMethod, $patchedMethod, [string]$label) {
    $pattern = '*TextBoxTextInputMethodClient::BeginChange()*'
    if ((Get-CallCount $patchedMethod $pattern) -ne (Get-CallCount $originalMethod $pattern) + 1) {
        throw "$label does not contain the expected new source-compiled batching call."
    }
    $beforeFinally = @($originalMethod.Body.ExceptionHandlers | Where-Object HandlerType -EQ 'Finally')
    $afterFinally = @($patchedMethod.Body.ExceptionHandlers | Where-Object HandlerType -EQ 'Finally')
    if ($afterFinally.Count -ne $beforeFinally.Count + 1) {
        throw "$label does not have the expected source-compiled using/finally scope."
    }
}

$referenceDir = Join-Path $WorkRoot 'verification/official'
New-Item -ItemType Directory -Path $referenceDir -Force | Out-Null
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $WorkRoot 'reference/Avalonia.12.1.3.nupkg'))
try {
    foreach ($tfm in @('net8.0', 'net10.0')) {
      foreach ($assembly in @('Avalonia.Base', 'Avalonia.Controls')) {
        $originalFile = Join-Path $referenceDir "$tfm.$assembly.dll"
        [IO.Compression.ZipFileExtensions]::ExtractToFile(
            $zip.GetEntry("lib/$tfm/$assembly.dll"), $originalFile, $true)
        $patchedFile = Join-Path $source "src/$assembly/bin/Release/$tfm/$assembly.dll"
        $original = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($originalFile)
        $patched = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($patchedFile)
        try {
            if ($original.Name.FullName -cne $patched.Name.FullName) {
                throw "$tfm assembly identity changed: $($patched.Name.FullName)"
            }
            if (-not $patched.MainModule.Attributes.HasFlag([Mono.Cecil.ModuleAttributes]::StrongNameSigned)) {
                throw "$tfm output is not strong-name signed."
            }
            if ([Convert]::ToHexString($original.Name.PublicKey) -cne [Convert]::ToHexString($patched.Name.PublicKey)) {
                throw "$tfm public key changed."
            }
            $before = @(Get-Definitions $original)
            $after = @(Get-Definitions $patched)
            $difference = @(Compare-Object $before $after -CaseSensitive)
            if ($difference.Count -ne 0) {
                $difference | Select-Object -First 20 | Format-List
                throw "$tfm assembly definition/reference contract changed."
            }
            if ($assembly -eq 'Avalonia.Controls') {
                $originalMethod = $original.MainModule.GetType('Avalonia.Controls.TextBox').Methods |
                    Where-Object Name -CEQ 'OnPropertyChanged'
                $patchedMethod = $patched.MainModule.GetType('Avalonia.Controls.TextBox').Methods |
                    Where-Object Name -CEQ 'OnPropertyChanged'
                Assert-Batching $originalMethod $patchedMethod "$tfm TextBox text change"
                $originalSetter = ($original.MainModule.GetType('Avalonia.Controls.TextBoxTextInputMethodClient').Properties |
                    Where-Object Name -CEQ 'Selection').SetMethod
                $patchedSetter = ($patched.MainModule.GetType('Avalonia.Controls.TextBoxTextInputMethodClient').Properties |
                    Where-Object Name -CEQ 'Selection').SetMethod
                Assert-Batching $originalSetter $patchedSetter "$tfm input-method selection setter"
                if ((Get-CallCount $originalSetter '*::RaiseSelectionChanged()*') -ne 1 -or
                    (Get-CallCount $patchedSetter '*::RaiseSelectionChanged()*') -ne 0) {
                    throw "$tfm selection setter still has an unexpected explicit notification."
                }
                Write-Output '  Both source-compiled TextBox batching scopes verified.'
            }
            else {
                $originalMethod = $original.MainModule.GetType('Avalonia.Media.TextFormatting.TextLineImpl').Methods |
                    Where-Object Name -CEQ 'GetTextBounds'
                $patchedMethod = $patched.MainModule.GetType('Avalonia.Media.TextFormatting.TextLineImpl').Methods |
                    Where-Object Name -CEQ 'GetTextBounds'
                $emptyPattern = '*System.Array::Empty<Avalonia.Media.TextFormatting.TextBounds>()*'
                if ((Get-CallCount $patchedMethod $emptyPattern) -ne (Get-CallCount $originalMethod $emptyPattern) + 1 -or
                    (Get-CallCount $originalMethod '*ArgumentOutOfRangeException::.ctor*') -ne 1 -or
                    (Get-CallCount $patchedMethod '*ArgumentOutOfRangeException::.ctor*') -ne 0) {
                    throw "$tfm does not contain the expected empty TextLine bounds return."
                }
                Write-Output '  Source-compiled empty TextLine bounds return verified.'
            }
            Write-Output "${tfm}: $($patched.Name.FullName)"
            Write-Output "  $($before.Count) definition/reference records unchanged."
            Write-Output "  SHA256 $((Get-FileHash $patchedFile -Algorithm SHA256).Hash)"
        }
        finally {
            $original.Dispose()
            $patched.Dispose()
        }
      }
    }
}
finally {
    $zip.Dispose()
}
