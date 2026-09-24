[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'companion\CodexWallpaperSkin.Companion.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') {
  throw "Project version must use numeric major.minor.patch form; found '$version'."
}

[xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'companion\app.manifest') -Raw
$manifestVersion = [string]$manifest.assembly.assemblyIdentity.version
if ($manifestVersion -ne "$version.0") {
  throw "Application manifest version '$manifestVersion' does not match project version '$version'."
}

$installer = Get-Content -LiteralPath (Join-Path $root 'installer\CodexWallpaperSkin.iss') -Raw
if (-not $installer.Contains("#define AppVersion `"$version`"", [StringComparison]::Ordinal)) {
  throw "Installer default version does not match project version '$version'."
}

foreach ($readme in @('README.md', 'README.en.md')) {
  $content = Get-Content -LiteralPath (Join-Path $root $readme) -Raw
  $versionMarker = '`' + $version + '`'
  if (-not $content.Contains($versionMarker, [StringComparison]::Ordinal)) {
    throw "$readme does not identify the current version as $version."
  }
}

Write-Output "Version consistency: PASS ($version)."
