[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$PreviousSetupPath,
  [Parameter(Mandatory = $true)][string]$CurrentSetupPath
)

$ErrorActionPreference = 'Stop'
$PreviousSetupPath = [IO.Path]::GetFullPath($PreviousSetupPath)
$CurrentSetupPath = [IO.Path]::GetFullPath($CurrentSetupPath)
foreach ($setupPath in @($PreviousSetupPath, $CurrentSetupPath)) {
  if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Installer was not found: $setupPath"
  }
}

function Get-ProductVersion {
  param([Parameter(Mandatory = $true)][string]$Path)
  $value = (Get-Item -LiteralPath $Path).VersionInfo.ProductVersion
  $normalized = ($value -split '[+-]')[0]
  $version = $null
  if (-not [version]::TryParse($normalized, [ref]$version)) {
    throw "Release file has no valid product version: $Path"
  }
  return $version
}

function Get-ProtectedStateSnapshot {
  $stateRoot = Join-Path $env:LOCALAPPDATA 'CodexWallpaperSkin'
  $snapshot = [ordered]@{}
  foreach ($leaf in @('state.json', 'library.json')) {
    $path = Join-Path $stateRoot $leaf
    $snapshot[$leaf] = if (Test-Path -LiteralPath $path -PathType Leaf) {
      (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    } else { '<missing>' }
  }
  return $snapshot
}

function Assert-ProtectedStateUnchanged {
  param([System.Collections.IDictionary]$Before, [string]$Stage)
  $after = Get-ProtectedStateSnapshot
  foreach ($leaf in $Before.Keys) {
    if ($Before[$leaf] -ne $after[$leaf]) {
      throw "$Stage changed the user's $leaf file."
    }
  }
}

function Invoke-CheckedProcess {
  param([string]$FilePath, [string[]]$Arguments, [string]$Label)
  $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -PassThru -Wait -WindowStyle Hidden
  if ($process.ExitCode -ne 0) { throw "$Label failed with exit code $($process.ExitCode)." }
}

function Assert-InstalledVersion {
  param([string]$ExecutablePath, [version]$ExpectedVersion, [string]$Stage)
  if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "$Stage did not leave an installed executable."
  }
  $actual = Get-ProductVersion -Path $ExecutablePath
  if ($actual -ne $ExpectedVersion) {
    throw "$Stage installed version $actual instead of $ExpectedVersion."
  }
  $registered = Get-ItemProperty -LiteralPath 'HKCU:\Software\CodexWallpaperSkin' -ErrorAction Stop
  if ([version]$registered.InstalledVersion -ne $ExpectedVersion) {
    throw "$Stage registered version '$($registered.InstalledVersion)' instead of '$ExpectedVersion'."
  }
}

$previousVersion = Get-ProductVersion -Path $PreviousSetupPath
$currentVersion = Get-ProductVersion -Path $CurrentSetupPath
if ($previousVersion -ge $currentVersion) {
  throw "Cross-version testing requires an older baseline; found $previousVersion -> $currentVersion."
}
if (Get-ItemProperty -LiteralPath 'HKCU:\Software\CodexWallpaperSkin' -ErrorAction SilentlyContinue) {
  throw 'A registered Codex Wallpaper Skin installation already exists. Refusing to disturb it.'
}
if (Get-Process -Name CodexWallpaperSkin -ErrorAction SilentlyContinue) {
  throw 'Codex Wallpaper Skin is running. Use Remove wallpaper and exit before the upgrade smoke test.'
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("CodexWallpaperSkin-upgrade-smoke-" + [Guid]::NewGuid().ToString('N'))
$testRoot = [IO.Path]::GetFullPath($testRoot)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'The upgrade smoke-test directory escaped the Windows temporary directory.'
}

$stateBefore = Get-ProtectedStateSnapshot
$installed = $false
$installedExecutable = Join-Path $testRoot 'CodexWallpaperSkin.exe'
$installArguments = @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS',
  "/DIR=$testRoot", '/LANG=chinesesimplified')
try {
  Invoke-CheckedProcess $PreviousSetupPath $installArguments "Install baseline $previousVersion"
  $installed = $true
  Assert-InstalledVersion $installedExecutable $previousVersion 'Baseline install'
  Assert-ProtectedStateUnchanged $stateBefore 'Baseline install'
  Invoke-CheckedProcess $installedExecutable @('--self-test') 'Baseline installed executable self-test'

  Invoke-CheckedProcess $CurrentSetupPath $installArguments "Upgrade to $currentVersion"
  Assert-InstalledVersion $installedExecutable $currentVersion 'Cross-version upgrade'
  Assert-ProtectedStateUnchanged $stateBefore 'Cross-version upgrade'
  Invoke-CheckedProcess $installedExecutable @('--self-test') 'Upgraded executable self-test'

  $uninstaller = Join-Path $testRoot 'unins000.exe'
  Invoke-CheckedProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') 'Upgraded silent uninstall'
  $installed = $false
  if (Test-Path -LiteralPath $installedExecutable) {
    throw 'The upgraded uninstaller left the application executable behind.'
  }
  if (Test-Path -LiteralPath 'HKCU:\Software\CodexWallpaperSkin') {
    throw 'The upgraded uninstaller left product registry metadata behind.'
  }
  Assert-ProtectedStateUnchanged $stateBefore 'Data-preserving uninstall'
  Write-Output "Cross-version installer smoke test: PASS ($previousVersion -> $currentVersion, state preserved, clean uninstall)."
}
finally {
  if ($installed) {
    $uninstaller = Join-Path $testRoot 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
      try {
        Invoke-CheckedProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') 'Cleanup uninstall'
      } catch { }
    }
  }
}
