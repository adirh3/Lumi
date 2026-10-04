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

function Get-LengthZeroCheckCount($method) {
    $count = 0
    $instructions = $method.Body.Instructions
    for ($i = 0; $i -lt $instructions.Count - 1; $i++) {
        if ($instructions[$i].OpCode.Code -eq 'Ldarg_2' -and
            $instructions[$i + 1].OpCode.Code -in @('Brfalse', 'Brfalse_S')) {
            $count++
        }
    }
    return $count
}

$referenceDir = Join-Path $WorkRoot 'verification/official'
New-Item -ItemType Directory -Path $referenceDir -Force | Out-Null
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $WorkRoot 'reference/Avalonia.12.1.2.nupkg'))
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
                $beforeCalls = @($originalMethod.Body.Instructions |
                    Where-Object { "$($_.Operand)" -like '*TextBoxTextInputMethodClient::BeginChange()*' })
                $afterCalls = @($patchedMethod.Body.Instructions |
                    Where-Object { "$($_.Operand)" -like '*TextBoxTextInputMethodClient::BeginChange()*' })
                if ($beforeCalls.Count -ne 0 -or $afterCalls.Count -ne 1) {
                    throw "$tfm does not contain exactly the expected new source-compiled batching call."
                }
                $finally = @($patchedMethod.Body.ExceptionHandlers |
                    Where-Object HandlerType -EQ 'Finally')
                if ($finally.Count -ne 1) {
                    throw "$tfm does not have the expected source-compiled using/finally scope."
                }
                Write-Output '  Source-compiled TextBox BeginChange/finally verified.'
            }
            else {
                $originalMethod = $original.MainModule.GetType('Avalonia.Media.TextFormatting.TextLayout').Methods |
                    Where-Object Name -CEQ 'HitTestTextRange'
                $patchedMethod = $patched.MainModule.GetType('Avalonia.Media.TextFormatting.TextLayout').Methods |
                    Where-Object Name -CEQ 'HitTestTextRange'
                $beforeChecks = Get-LengthZeroCheckCount $originalMethod
                $afterChecks = Get-LengthZeroCheckCount $patchedMethod
                if ($beforeChecks -ne 0 -or $afterChecks -ne 2) {
                    throw "$tfm does not have both source-compiled TextLayout zero-length guards."
                }
                Write-Output '  Both source-compiled TextLayout zero-length guards verified.'
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
