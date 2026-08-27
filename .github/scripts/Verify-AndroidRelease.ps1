[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$ApkPath,
    [string]$ReleaseVersion,
    [string]$AndroidVersionCode,
    [string]$ExpectedFingerprintPath,
    [string]$AndroidHome,
    [string]$ReleaseDirectory = "Releases"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-NormalizedSha256 {
    param(
        [AllowEmptyString()]
        [string]$Value,
        [string]$Description
    )

    $normalized = $Value -replace '[:\s-]', ''
    if ($normalized -notmatch '^[0-9a-fA-F]{64}$') {
        throw "$Description is missing or malformed."
    }

    return $normalized.ToLowerInvariant()
}

function Invoke-NativeText {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [switch]$ReportCapture
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Failed to start $([IO.Path]::GetFileName($FilePath))."
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    if ($ReportCapture) {
        Write-Host "$([IO.Path]::GetFileName($FilePath)) capture: stdout=$($stdout.Length) chars, stderr=$($stderr.Length) chars."
        foreach ($stream in @(
            @{ Name = "stdout"; Text = $stdout },
            @{ Name = "stderr"; Text = $stderr }
        )) {
            foreach ($line in ($stream.Text -split "`n")) {
                if ($line -match '(?i)Signer\s+#1\s+certificate\s+SHA-?256\s+digest') {
                    $safeLine = $line -replace '(?i)(digest\s*:\s*).+$', '$1<fingerprint>'
                    $safeLine = $safeLine.Replace("`r", "<CR>").Replace("`t", "<TAB>")
                    Write-Host "$([IO.Path]::GetFileName($FilePath)) $($stream.Name) signer line: <$safeLine>"
                }
            }
        }
    }

    $text = (@($stdout, $stderr) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n"
    $text = $text -replace "`r`n?", "`n"
    if ($exitCode -ne 0) {
        throw "$([IO.Path]::GetFileName($FilePath)) failed with exit code $exitCode."
    }
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "$([IO.Path]::GetFileName($FilePath)) produced no verification output."
    }

    return $text
}

function Assert-ApkSignerOutput {
    param(
        [string]$Output,
        [AllowEmptyString()]
        [string]$ExpectedFingerprint
    )

    if (($Output -notmatch '(?im)^\s*Verified\s+using\s+v2\s+scheme\b[^:\r\n]*:\s*true\s*$') -or
        ($Output -notmatch '(?im)^\s*Verified\s+using\s+v3\s+scheme\b[^:\r\n]*:\s*true\s*$')) {
        throw "Android APK signature verification did not confirm both v2 and v3 signatures."
    }

    $fingerprintMatch = [regex]::Match(
        $Output,
        '(?im)^.*?\bSigner\s+#1\s+certificate\s+SHA-?256\s+digest\s*:\s*((?:[0-9a-f]{2}[: \t-]?){31}[0-9a-f]{2})[ \t]*\r?$')
    if (-not $fingerprintMatch.Success) {
        throw "Android signer SHA-256 fingerprint is missing from apksigner output."
    }

    $actualFingerprint = ConvertTo-NormalizedSha256 `
        -Value $fingerprintMatch.Groups[1].Value `
        -Description "Android signer SHA-256 fingerprint"
    if (-not [string]::IsNullOrWhiteSpace($ExpectedFingerprint)) {
        $expected = ConvertTo-NormalizedSha256 `
            -Value $ExpectedFingerprint `
            -Description "Expected Android signer SHA-256 fingerprint"
        if ($actualFingerprint -cne $expected) {
            throw "Android signer fingerprint does not match the expected production certificate."
        }
    }

    return $actualFingerprint
}

function Assert-ApkBadging {
    param(
        [string]$Output,
        [string]$Version,
        [string]$VersionCode
    )

    $escapedVersion = [regex]::Escape($Version)
    $escapedVersionCode = [regex]::Escape($VersionCode)
    if (($Output -notmatch "(?m)^\s*package:\s+name='com\.lumi\.mobile'(?:\s|$)") -or
        ($Output -notmatch "(?m)^package:.*\bversionName='$escapedVersion'(?:\s|$)") -or
        ($Output -notmatch "(?m)^package:.*\bversionCode='$escapedVersionCode'(?:\s|$)") -or
        ($Output -notmatch "(?m)^\s*native-code:\s+'arm64-v8a'\s*$")) {
        throw "Android APK package, version, versionCode, or ABI verification failed."
    }
}

function Assert-AndroidManifestBackupPolicy {
    param([string]$Output)

    $androidNamespace = '(?:https?://schemas\.android\.com/apk/res/android:|android:)'
    $backupDisabled = "(?im)^\s*A:\s+$androidNamespace" +
        'allowBackup(?:\([^)]*\))?\s*=\s*(?:false|\(type\s+0x12\)0x0)\s*$'
    if ($Output -notmatch $backupDisabled) {
        throw "Android manifest backup-disabled policy verification failed."
    }
}

function Assert-SelfTestFailure {
    param(
        [scriptblock]$Action,
        [string]$ExpectedMessage
    )

    $failed = $false
    try {
        & $Action | Out-Null
    }
    catch {
        $failed = $true
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Android verifier self-test received an unexpected error: $($_.Exception.Message)"
        }
    }

    if (-not $failed) {
        throw "Android verifier self-test accepted invalid input: $ExpectedMessage"
    }
}

function Invoke-SelfTest {
    $fingerprint = -join (1..32 | ForEach-Object { "ab" })
    $formattedFingerprint = (0..31 | ForEach-Object {
        $fingerprint.Substring($_ * 2, 2).ToUpperInvariant()
    }) -join ':'
    $signerOutput = @"
Verified using v2 scheme (APK Signature Scheme v2): true
Verified using v3 scheme (APK Signature Scheme v3): true
Signer #1 certificate SHA256 digest : $formattedFingerprint
"@
    $parsed = Assert-ApkSignerOutput -Output $signerOutput -ExpectedFingerprint $fingerprint
    if ($parsed -cne $fingerprint) {
        throw "Android verifier self-test did not normalize the signer fingerprint."
    }

    Assert-SelfTestFailure `
        -Action { Assert-ApkSignerOutput -Output ($signerOutput -replace '(?m)^Signer.*$', '') -ExpectedFingerprint "" } `
        -ExpectedMessage "fingerprint is missing"
    Assert-SelfTestFailure `
        -Action { Assert-ApkSignerOutput -Output ($signerOutput -replace $formattedFingerprint, 'not-a-fingerprint') -ExpectedFingerprint "" } `
        -ExpectedMessage "fingerprint is missing"
    Assert-SelfTestFailure `
        -Action { Assert-ApkSignerOutput -Output ($signerOutput -replace $formattedFingerprint, "$formattedFingerprint`:AB") -ExpectedFingerprint "" } `
        -ExpectedMessage "fingerprint is missing"
    Assert-SelfTestFailure `
        -Action { Assert-ApkSignerOutput -Output ($signerOutput -replace 'v3 scheme \(APK Signature Scheme v3\): true', 'v3 scheme (APK Signature Scheme v3): false') -ExpectedFingerprint "" } `
        -ExpectedMessage "both v2 and v3"
    Assert-SelfTestFailure `
        -Action { Assert-ApkSignerOutput -Output $signerOutput -ExpectedFingerprint (-join (1..32 | ForEach-Object { "cd" })) } `
        -ExpectedMessage "does not match"

    $badging = @"
package: name='com.lumi.mobile' versionCode='9008' versionName='0.9.8' compileSdkVersion='36'
native-code: 'arm64-v8a'
"@
    Assert-ApkBadging -Output $badging -Version "0.9.8" -VersionCode "9008"
    Assert-SelfTestFailure `
        -Action { Assert-ApkBadging -Output ($badging -replace 'com\.lumi\.mobile', 'com.example.app') -Version "0.9.8" -VersionCode "9008" } `
        -ExpectedMessage "package, version, versionCode, or ABI"

    $manifest = @"
A: http://schemas.android.com/apk/res/android:allowBackup(0x01010280)=false
"@
    Assert-AndroidManifestBackupPolicy -Output $manifest
    Assert-SelfTestFailure `
        -Action { Assert-AndroidManifestBackupPolicy -Output ($manifest -replace '=false', '=true') } `
        -ExpectedMessage "backup-disabled policy"

    $captureRoot = Join-Path ([IO.Path]::GetTempPath()) "lumi-apksigner-stream-$([Guid]::NewGuid())"
    $captureScript = Join-Path $captureRoot "fake-apksigner.ps1"
    $captureWrapper = Join-Path $captureRoot "fake-apksigner.bat"
    try {
        New-Item -ItemType Directory -Path $captureRoot | Out-Null
        @"
Write-Output 'Verified using v2 scheme (APK Signature Scheme v2): true'
Write-Output 'Verified using v3 scheme (APK Signature Scheme v3): true'
[Console]::Error.Write("apksigner.bat : Signer #1 certificate SHA-256 digest: $fingerprint`r`n")
"@ | Set-Content -LiteralPath $captureScript
        @"
@echo off
"$((Join-Path $PSHOME "pwsh.exe"))" -NoProfile -File "%~dp0fake-apksigner.ps1"
"@ | Set-Content -LiteralPath $captureWrapper
        $captured = Invoke-NativeText `
            -FilePath $captureWrapper `
            -Arguments @()
        Assert-ApkSignerOutput -Output $captured -ExpectedFingerprint $fingerprint | Out-Null

        "[Console]::Error.WriteLine('expected failure'); exit 7" |
            Set-Content -LiteralPath $captureScript
        Assert-SelfTestFailure `
            -Action { Invoke-NativeText -FilePath $captureWrapper -Arguments @() } `
            -ExpectedMessage "exit code 7"
    }
    finally {
        Remove-Item -LiteralPath $captureRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Output "Android release verifier self-test passed."
}

if ($SelfTest) {
    Invoke-SelfTest
    return
}

foreach ($required in @{
    ApkPath = $ApkPath
    ReleaseVersion = $ReleaseVersion
    AndroidVersionCode = $AndroidVersionCode
    ExpectedFingerprintPath = $ExpectedFingerprintPath
    AndroidHome = $AndroidHome
}.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($required.Value)) {
        throw "$($required.Key) is required."
    }
}

if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) {
    throw "Signed Android APK was not produced."
}
if (-not (Test-Path -LiteralPath $ExpectedFingerprintPath -PathType Leaf)) {
    throw "Expected Android signer fingerprint file was not produced."
}

$buildTools = Get-ChildItem (Join-Path $AndroidHome "build-tools") -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if (-not $buildTools) {
    throw "Android SDK build-tools were not found."
}

$apksigner = Join-Path $buildTools.FullName "apksigner.bat"
$aapt = Join-Path $buildTools.FullName "aapt.exe"
$aapt2 = Join-Path $buildTools.FullName "aapt2.exe"
foreach ($tool in @($apksigner, $aapt, $aapt2)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "Required Android verification tool was not found: $tool"
    }
}

$verificationText = Invoke-NativeText `
    -FilePath $apksigner `
    -Arguments @("verify", "--verbose", "--print-certs", $ApkPath) `
    -ReportCapture
$expectedFingerprint = [string](Get-Content -LiteralPath $ExpectedFingerprintPath -Raw)
$fingerprint = Assert-ApkSignerOutput `
    -Output $verificationText `
    -ExpectedFingerprint $expectedFingerprint.Trim()

$badgingText = Invoke-NativeText `
    -FilePath $aapt `
    -Arguments @("dump", "badging", $ApkPath)
Assert-ApkBadging `
    -Output $badgingText `
    -Version $ReleaseVersion `
    -VersionCode $AndroidVersionCode

$manifestText = Invoke-NativeText `
    -FilePath $aapt2 `
    -Arguments @("dump", "xmltree", "--file", "AndroidManifest.xml", $ApkPath)
Assert-AndroidManifestBackupPolicy -Output $manifestText

New-Item -ItemType Directory -Force $ReleaseDirectory | Out-Null
$target = Join-Path $ReleaseDirectory "Lumi-$ReleaseVersion-android-arm64.apk"
Copy-Item -LiteralPath $ApkPath -Destination $target -Force
@(
    "SHA256=$((Get-FileHash $target -Algorithm SHA256).Hash)"
    "SIGNER_SHA256=$fingerprint"
    "VERSION=$ReleaseVersion"
    "VERSION_CODE=$AndroidVersionCode"
    "ABI=arm64-v8a"
) | Set-Content (Join-Path $ReleaseDirectory "Lumi-$ReleaseVersion-android-arm64.txt")

Write-Output "Android APK signature, identity, package metadata, ABI, and backup policy verified."
