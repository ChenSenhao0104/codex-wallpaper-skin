[CmdletBinding(SupportsShouldProcess = $true)]
param(
  [switch]$Release,
  [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$skillRoot = Split-Path -Parent $PSScriptRoot
$configuration = if ($Release) { 'Release' } else { 'Debug' }
$portableExecutable = Join-Path $skillRoot 'dist\win-x64\CodexWallpaperSkin.exe'
$developmentExecutable = Join-Path $skillRoot "companion\bin\$configuration\net8.0-windows10.0.19041.0\win-x64\CodexWallpaperSkin.exe"
$developmentAssembly = Join-Path (Split-Path -Parent $developmentExecutable) 'CodexWallpaperSkin.dll'
$runtimeConfig = Join-Path (Split-Path -Parent $developmentExecutable) 'CodexWallpaperSkin.runtimeconfig.json'
$depsFile = Join-Path (Split-Path -Parent $developmentExecutable) 'CodexWallpaperSkin.deps.json'
$latestSource = Get-ChildItem -LiteralPath (Join-Path $skillRoot 'companion') -File -Recurse |
  Where-Object {
    $_.FullName -notmatch '\\(?:bin|obj)\\' -and
    $_.Extension -in @('.cs', '.xaml', '.csproj', '.manifest', '.js')
  } |
  Sort-Object LastWriteTimeUtc -Descending |
  Select-Object -First 1
$portableCurrent = (Test-Path -LiteralPath $portableExecutable -PathType Leaf) -and
  (-not $latestSource -or (Get-Item -LiteralPath $portableExecutable).LastWriteTimeUtc -ge $latestSource.LastWriteTimeUtc)
$developmentComplete = (Test-Path -LiteralPath $developmentExecutable -PathType Leaf) -and
  (Test-Path -LiteralPath $developmentAssembly -PathType Leaf) -and
  (Test-Path -LiteralPath $runtimeConfig -PathType Leaf) -and
  (Test-Path -LiteralPath $depsFile -PathType Leaf)
$developmentStale = $developmentComplete -and $latestSource -and
  ((Get-Item -LiteralPath $developmentAssembly).LastWriteTimeUtc -lt $latestSource.LastWriteTimeUtc)

if ($portableCurrent) {
  $executable = $portableExecutable
} else {
  $executable = $developmentExecutable
}

if (-not $portableCurrent -and (-not $developmentComplete -or $developmentStale)) {
  if ($NoBuild) { throw "Companion output is missing or older than its source: $developmentExecutable" }
  & (Join-Path $PSScriptRoot 'build-companion.ps1') -Configuration $configuration
  $developmentComplete = (Test-Path -LiteralPath $developmentExecutable -PathType Leaf) -and
    (Test-Path -LiteralPath $developmentAssembly -PathType Leaf) -and
    (Test-Path -LiteralPath $runtimeConfig -PathType Leaf) -and
    (Test-Path -LiteralPath $depsFile -PathType Leaf)
}
if (-not $portableCurrent -and -not $developmentComplete) {
  throw "Build completed without all required runtime files beside: $developmentExecutable"
}
if (-not $portableCurrent -and $latestSource -and
    (Get-Item -LiteralPath $developmentAssembly).LastWriteTimeUtc -lt $latestSource.LastWriteTimeUtc) {
  throw "Build completed, but the companion assembly is still older than its source: $developmentAssembly"
}

# This is the user-facing adjustment window, so a visible process is intentional.
if ($PSCmdlet.ShouldProcess($executable, 'Start the Codex Wallpaper Skin adjustment window')) {
  Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable)
}
