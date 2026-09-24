[CmdletBinding()]
param(
  [string]$InstallerScript
)

$ErrorActionPreference = 'Stop'
$skillRoot = Split-Path -Parent $PSScriptRoot
if (-not $InstallerScript) {
  $InstallerScript = Join-Path $skillRoot 'installer\CodexWallpaperSkin.iss'
}
$InstallerScript = [IO.Path]::GetFullPath($InstallerScript)
if (-not (Test-Path -LiteralPath $InstallerScript -PathType Leaf)) {
  throw "Installer script was not found: $InstallerScript"
}

$content = Get-Content -LiteralPath $InstallerScript -Raw
$required = [ordered]@{
  'stable product identity' = 'AppId={{2E164A64-2E9C-4BCA-A461-D1DD71C6032F}'
  'current-user install' = 'PrivilegesRequired=lowest'
  'current-user program directory' = 'DefaultDirName={localappdata}\Programs\Codex Wallpaper Skin'
  'Windows 11 floor' = 'MinVersion=10.0.22000'
  'no automatic application close' = 'CloseApplications=no'
  'no automatic restart' = 'RestartApplications=no'
  'GUI mutex guard' = 'Local\CodexWallpaperSkin.Companion.Gui'
  'worker mutex guard' = 'Local\CodexWallpaperSkin.DeferredRestore.v2'
  'English installer language' = 'Name: "english"'
  'Simplified Chinese installer language' = 'Name: "chinesesimplified"'
  'owned startup cleanup' = 'IsOwnedStartupCommand'
  'bounded user-data deletion' = "DelTree(ExpandConstant('{localappdata}\CodexWallpaperSkin'), True, True, True)"
}
foreach ($item in $required.GetEnumerator()) {
  if (-not $content.Contains($item.Value, [StringComparison]::Ordinal)) {
    throw "Installer policy check failed: missing $($item.Key)."
  }
}

$forbiddenPatterns = [ordered]@{
  'process termination' = '(?i)taskkill|Stop-Process|TerminateProcess|Process\.Kill'
  'broad install deletion' = '(?im)^\s*\[(InstallDelete|UninstallDelete)\]'
  'machine-wide privilege' = '(?im)^\s*PrivilegesRequired\s*=\s*admin'
  'Codex launch or shutdown' = '(?i)(Filename|Parameters)\s*:.*(OpenAI\.Codex|ChatGPT\.exe|Codex\.exe)'
}
foreach ($item in $forbiddenPatterns.GetEnumerator()) {
  if ($content -match $item.Value) {
    throw "Installer policy check failed: forbidden $($item.Key) behavior was found."
  }
}

Write-Output 'Installer policy: PASS (per-user, bilingual, no forced close/restart, bounded cleanup).'
