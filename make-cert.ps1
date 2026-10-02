# GCalendarSync - create the package signing certificate.
#
# The .pfx IS committed for this app (see README.md - a test app that never goes to the Store), so a
# clone normally already has one and this script does nothing. build.bat calls it only when the file
# is missing.
#
# Running it by hand to REPLACE the certificate is not a small thing: a package signed by a new key
# cannot upgrade one signed by the old, so every phone with the app installed has to uninstall it
# first, losing its synced contacts and sync state.
#
# The subject MUST equal the manifest's Publisher exactly (CN=GCalendarSync - the identity kept
# from before the rename, see Package.appxmanifest) or signing is
# rejected. The .cer beside it is what gets installed on the phone to trust the package.

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dir  = Join-Path $root "src\GCalendarSync"
$pfx  = Join-Path $dir "GCalendarSync_TemporaryKey.pfx"
$cer  = Join-Path $dir "GCalendarSync_TemporaryKey.cer"

if (Test-Path $pfx) {
    Write-Output "signing certificate already present: $pfx"
    exit 0
}

Write-Output "creating a package signing certificate (CN=GCalendarSync)..."

$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=GCalendarSync" `
    -KeyUsage DigitalSignature -FriendlyName "GCalendarSync dev signing" `
    -CertStoreLocation "Cert:\CurrentUser\My" -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(10) `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}Subject Type:End Entity")

# Empty-password PFX, matching how Visual Studio emits a _TemporaryKey.pfx.
[System.IO.File]::WriteAllBytes(
    $pfx, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, ""))
[System.IO.File]::WriteAllBytes(
    $cer, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

Write-Output ("thumbprint: " + $cert.Thumbprint)
Write-Output "wrote $pfx"
Write-Output "wrote $cer"
Write-Output ""
Write-Output "NOTE: this is a NEW certificate. The phone must trust it before the app will"
Write-Output "install - deploy the .cer from AppPackages once, before the .appx."
