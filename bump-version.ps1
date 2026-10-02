# GCalendarSync - bump the package version.
#
# WP8.1 will not replace an installed app with a package of the SAME version: the deployment
# tool reports success and the phone quietly keeps running the old binary, so a fix looks like
# it did nothing. So the version moves on every build.
#
# Scheme: 1.0.<build>.0 - the third digit is the one that increments.

param(
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$root     = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifest = Join-Path $root "src\GCalendarSync\Package.appxmanifest"
$asmInfo  = Join-Path $root "src\GCalendarSync\Properties\AssemblyInfo.cs"

if (-not (Test-Path $manifest)) { throw "manifest not found: $manifest" }

$text = Get-Content $manifest -Raw

if ($text -notmatch '(<Identity[^>]*Version=")(\d+)\.(\d+)\.(\d+)\.(\d+)(")') {
    throw "could not find an <Identity ... Version=""x.y.z.w""> in the manifest"
}

$major = [int]$Matches[2]
$minor = [int]$Matches[3]
$build = [int]$Matches[4]
$rev   = [int]$Matches[5]

$old = "$major.$minor.$build.$rev"
$build++
$new = "$major.$minor.$build.$rev"

# Only the Version inside <Identity ...> - not any other Version attribute in the file.
$text = $text -replace '(<Identity[^>]*Version=")\d+\.\d+\.\d+\.\d+(")', "`${1}$new`${2}"
Set-Content -Path $manifest -Value $text -Encoding UTF8 -NoNewline

# Keep the assembly version in step so the built binary is identifiable too.
if (Test-Path $asmInfo) {
    $a = Get-Content $asmInfo -Raw
    $a = $a -replace 'AssemblyVersion\("\d+\.\d+\.\d+\.\d+"\)',     "AssemblyVersion(""$new"")"
    $a = $a -replace 'AssemblyFileVersion\("\d+\.\d+\.\d+\.\d+"\)', "AssemblyFileVersion(""$new"")"
    Set-Content -Path $asmInfo -Value $a -Encoding UTF8 -NoNewline
}

if (-not $Quiet) { Write-Output "version $old -> $new" }
