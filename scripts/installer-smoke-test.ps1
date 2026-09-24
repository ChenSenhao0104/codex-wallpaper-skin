[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$SetupPath
)

$ErrorActionPreference = 'Stop'
$SetupPath = [IO.Path]::GetFullPath($SetupPath)
if (-not (Test-Path -LiteralPath $SetupPath -PathType Leaf)) {
  throw "Installer was not found: $SetupPath"
}

$existingProduct = Get-ItemProperty -LiteralPath 'HKCU:\Software\CodexWallpaperSkin' -ErrorAction SilentlyContinue
if ($existingProduct) {
  throw "A registered Codex Wallpaper Skin installation already exists at '$($existingProduct.InstallLocation)'. Refusing to disturb it."
}
if (Get-Process -Name CodexWallpaperSkin -ErrorAction SilentlyContinue) {
  throw 'Codex Wallpaper Skin is running. Use Remove wallpaper and exit before the installer smoke test.'
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("CodexWallpaperSkin-installer-smoke-" + [Guid]::NewGuid().ToString('N'))
$testRoot = [IO.Path]::GetFullPath($testRoot)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'The installer smoke-test directory escaped the Windows temporary directory.'
}
$statePath = Join-Path $env:LOCALAPPDATA 'CodexWallpaperSkin\state.json'
$stateHashBefore = if (Test-Path -LiteralPath $statePath) {
  (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash
} else { $null }

function Invoke-CheckedProcess {
  param([string]$FilePath, [string[]]$Arguments, [string]$Label)
  $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -PassThru -Wait -WindowStyle Hidden
  if ($process.ExitCode -ne 0) { throw "$Label failed with exit code $($process.ExitCode)." }
}

$installed = $false
try {
  $installArguments = @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS',
    "/DIR=$testRoot", '/LANG=chinesesimplified')
  Invoke-CheckedProcess $SetupPath $installArguments 'Clean install'
  $installed = $true

  $installedExecutable = Join-Path $testRoot 'CodexWallpaperSkin.exe'
  if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
    throw 'The installed executable is missing.'
  }
  Invoke-CheckedProcess $installedExecutable @('--self-test') 'Installed executable self-test'
  Invoke-CheckedProcess $SetupPath $installArguments 'In-place upgrade'

  $registered = Get-ItemProperty -LiteralPath 'HKCU:\Software\CodexWallpaperSkin' -ErrorAction Stop
  if ($registered.InstallLocation.TrimEnd('\') -ne $testRoot) {
    throw 'The installer registered an unexpected installation directory.'
  }

  $uninstaller = Join-Path $testRoot 'unins000.exe'
  Invoke-CheckedProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') 'Silent uninstall'
  $installed = $false
  if (Test-Path -LiteralPath $installedExecutable) {
    throw 'The uninstaller left the application executable behind.'
  }
  if (Test-Path -LiteralPath 'HKCU:\Software\CodexWallpaperSkin') {
    throw 'The uninstaller left product registry metadata behind.'
  }

  $stateHashAfter = if (Test-Path -LiteralPath $statePath) {
    (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash
  } else { $null }
  if ($stateHashBefore -ne $stateHashAfter) {
    throw 'A data-preserving uninstall changed the existing user state.'
  }
  Write-Output 'Installer smoke test: PASS (clean install, self-test, in-place upgrade, preserving uninstall).'
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
