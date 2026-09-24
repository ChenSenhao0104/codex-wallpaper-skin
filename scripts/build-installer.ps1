[CmdletBinding()]
param(
  [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
  [string]$IsccPath,
  [ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$CertificateThumbprint,
  [ValidatePattern('^https://')][string]$TimestampUrl,
  [string]$SignToolPath,
  [switch]$SkipPortableBuild
)

$ErrorActionPreference = 'Stop'
$skillRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $skillRoot 'companion\CodexWallpaperSkin.Companion.csproj'
$installerScript = Join-Path $skillRoot 'installer\CodexWallpaperSkin.iss'
$distRoot = [IO.Path]::GetFullPath((Join-Path $skillRoot 'dist')).TrimEnd('\')

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = [string]$project.Project.PropertyGroup.Version | Select-Object -First 1
if (-not $Version) { $Version = $projectVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
  throw "The companion version must use numeric major.minor.patch form; found '$Version'."
}
if ($Version -ne $projectVersion) {
  throw "Installer version '$Version' does not match companion version '$projectVersion'."
}

& (Join-Path $PSScriptRoot 'installer-policy-test.ps1') -InstallerScript $installerScript
& (Join-Path $PSScriptRoot 'version-consistency-test.ps1')

$signingRequested = [bool]$CertificateThumbprint -or [bool]$TimestampUrl
if ($signingRequested -and (-not $CertificateThumbprint -or -not $TimestampUrl)) {
  throw 'Code signing requires both -CertificateThumbprint and -TimestampUrl.'
}

$payloadName = "win-x64-v$Version-installer-payload"
$archiveBaseName = "CodexWallpaperSkin-v$Version-portable-win-x64"
$payloadDirectory = Join-Path $distRoot $payloadName
$portableArchive = Join-Path $distRoot "$archiveBaseName.zip"
$setupLeaf = "CodexWallpaperSkin-Setup-v$Version-win-x64.exe"
$setupPath = Join-Path $distRoot $setupLeaf
$releaseManifestPath = Join-Path $distRoot "CodexWallpaperSkin-v$Version-release-manifest.json"

if (-not $SkipPortableBuild) {
  & (Join-Path $PSScriptRoot 'build-companion.ps1') -Publish `
    -PublishDirectoryName $payloadName -ArchiveBaseName $archiveBaseName
}

$payloadExecutable = Join-Path $payloadDirectory 'CodexWallpaperSkin.exe'
foreach ($required in @($installerScript, $payloadExecutable, (Join-Path $payloadDirectory 'LICENSE'), $portableArchive)) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
    throw "Required installer input was not found: $required"
  }
}

if ($signingRequested) {
  & (Join-Path $PSScriptRoot 'sign-release.ps1') -Path $payloadExecutable `
    -CertificateThumbprint $CertificateThumbprint -TimestampUrl $TimestampUrl -SignToolPath $SignToolPath

  foreach ($artifact in @($portableArchive, "$portableArchive.sha256")) {
    if (Test-Path -LiteralPath $artifact) { Remove-Item -LiteralPath $artifact -Force }
  }
  Compress-Archive -Path (Join-Path $payloadDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal
  $portableChecksum = (Get-FileHash -LiteralPath $portableArchive -Algorithm SHA256).Hash.ToLowerInvariant()
  Set-Content -LiteralPath "$portableArchive.sha256" `
    -Value "$portableChecksum  $(Split-Path -Leaf $portableArchive)" -Encoding ascii
}

function Resolve-Iscc {
  param([string]$ExplicitPath)
  if ($ExplicitPath) {
    $resolved = [IO.Path]::GetFullPath($ExplicitPath)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
      throw "Inno Setup compiler was not found: $resolved"
    }
    return $resolved
  }

  $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($command) { return $command.Source }

  $candidateRoots = @(
    ${env:ProgramFiles(x86)},
    $env:ProgramFiles,
    (Join-Path $env:LOCALAPPDATA 'Programs')
  ) | Where-Object { $_ }
  foreach ($root in $candidateRoots) {
    foreach ($relative in @('Inno Setup 7\ISCC.exe', 'Inno Setup 7 x64\ISCC.exe',
        'Inno Setup 6\ISCC.exe', 'Inno Setup\ISCC.exe')) {
      $candidate = Join-Path $root $relative
      if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
  }
  throw 'Inno Setup 7 was not found. Install JRSoftware.InnoSetup.7 or pass -IsccPath.'
}

$iscc = Resolve-Iscc -ExplicitPath $IsccPath
if (Test-Path -LiteralPath $setupPath) {
  $setupItem = Get-Item -LiteralPath $setupPath -Force
  if ($setupItem.PSIsContainer -or
      ($setupItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $setupItem.LinkType) {
    throw "Refusing to replace a directory or reparse point as an installer: $setupPath"
  }
  Remove-Item -LiteralPath $setupPath -Force
}
if (Test-Path -LiteralPath "$setupPath.sha256") {
  Remove-Item -LiteralPath "$setupPath.sha256" -Force
}

& $iscc "/DAppVersion=$Version" "/DSourceDir=$payloadDirectory" "/DOutputDir=$distRoot" $installerScript
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
  throw "Inno Setup did not create the expected installer: $setupPath"
}

if ($signingRequested) {
  & (Join-Path $PSScriptRoot 'sign-release.ps1') -Path $setupPath `
    -CertificateThumbprint $CertificateThumbprint -TimestampUrl $TimestampUrl -SignToolPath $SignToolPath
}

$checksum = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$setupPath.sha256" -Value "$checksum  $setupLeaf" -Encoding ascii

& (Join-Path $PSScriptRoot 'write-release-manifest.ps1') -Version $Version `
  -ArtifactPath @($setupPath, $portableArchive) -OutputPath $releaseManifestPath

Write-Host "Installer: $setupPath"
Write-Host "SHA-256: $checksum"
