[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
  [switch]$PurgeLocalState,
  [switch]$Force
)

$ErrorActionPreference = 'Stop'
$skillRoot = Split-Path -Parent $PSScriptRoot
$candidates = @(
  (Join-Path $skillRoot 'dist\win-x64\CodexWallpaperSkin.exe'),
  (Join-Path $skillRoot 'companion\bin\Release\net8.0-windows\win-x64\CodexWallpaperSkin.exe'),
  (Join-Path $skillRoot 'companion\bin\Debug\net8.0-windows\win-x64\CodexWallpaperSkin.exe')
)
$latestSource = Get-ChildItem -LiteralPath (Join-Path $skillRoot 'companion') -File -Recurse |
  Where-Object {
    $_.FullName -notmatch '\\(?:bin|obj)\\' -and
    $_.Extension -in @('.cs', '.xaml', '.csproj', '.manifest', '.js')
  } |
  Sort-Object LastWriteTimeUtc -Descending |
  Select-Object -First 1

function Test-FreshCompanion {
  param([Parameter(Mandatory = $true)][string]$ExecutablePath)
  if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { return $false }
  $isDevelopmentOutput = $ExecutablePath -match '\\companion\\bin\\(?:Debug|Release)\\'
  $freshnessTarget = $ExecutablePath
  if ($isDevelopmentOutput) {
    $directory = Split-Path -Parent $ExecutablePath
    $assembly = Join-Path $directory 'CodexWallpaperSkin.dll'
    $runtimeConfig = Join-Path $directory 'CodexWallpaperSkin.runtimeconfig.json'
    $depsFile = Join-Path $directory 'CodexWallpaperSkin.deps.json'
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf) -or
        -not (Test-Path -LiteralPath $runtimeConfig -PathType Leaf) -or
        -not (Test-Path -LiteralPath $depsFile -PathType Leaf)) { return $false }
    $freshnessTarget = $assembly
  }
  return (-not $latestSource -or (Get-Item -LiteralPath $freshnessTarget).LastWriteTimeUtc -ge $latestSource.LastWriteTimeUtc)
}

$existingCandidates = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
$executable = $existingCandidates |
  Where-Object { Test-FreshCompanion -ExecutablePath $_ } |
  Select-Object -First 1
if ($executable) {
  if ($PSCmdlet.ShouldProcess('the verified Codex renderer at the saved loopback endpoint', 'Remove the Codex Wallpaper Skin runtime')) {
    $restoreProcess = Start-Process -FilePath $executable -ArgumentList '--restore' -PassThru -WindowStyle Hidden
    if (-not $restoreProcess.WaitForExit(45000)) {
      try { $restoreProcess.Kill() } catch {}
      throw 'Restore helper exceeded the 45-second safety timeout and was stopped.'
    }
    $restoreProcess.Refresh()
    if ($restoreProcess.ExitCode -ne 0) { throw "Restore command failed with exit code $($restoreProcess.ExitCode)." }
  }
} else {
  if ($existingCandidates.Count -gt 0) {
    throw 'All companion executables are older than the current source. Rebuild first; no live CDP cleanup was performed.'
  }
  throw 'Companion executable is not built. Build it first; no live CDP cleanup was performed.'
}

if ($PurgeLocalState) {
  $stateRoot = Join-Path $env:LOCALAPPDATA 'CodexWallpaperSkin'
  $expectedParent = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\')
  $target = [IO.Path]::GetFullPath($stateRoot).TrimEnd('\')
  if ([IO.Path]::GetDirectoryName($target).TrimEnd('\') -cne $expectedParent -or
      [IO.Path]::GetFileName($target) -cne 'CodexWallpaperSkin') {
    throw "Refusing to purge an unexpected state path: $target"
  }
  if (Test-Path -LiteralPath $target) {
    $stateItem = Get-Item -LiteralPath $target -Force
    if (($stateItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $stateItem.LinkType) {
      throw "Refusing to purge a reparse-point state directory: $target"
    }
    $nestedLink = Get-ChildItem -LiteralPath $target -Force -Recurse -ErrorAction Stop |
      Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.LinkType } |
      Select-Object -First 1
    if ($nestedLink) {
      throw "Refusing to purge state containing a reparse point: $($nestedLink.FullName)"
    }
    if ($PSCmdlet.ShouldProcess($target, 'Delete Codex Wallpaper Skin local presets and recovery files')) {
      if ($Force -or $PSCmdlet.ShouldContinue("Delete only this project's local presets and recovery files?", 'Confirm local state purge')) {
        Remove-Item -LiteralPath $target -Recurse -Force
        Write-Host "Removed local state: $target"
      }
    }
  }
}
