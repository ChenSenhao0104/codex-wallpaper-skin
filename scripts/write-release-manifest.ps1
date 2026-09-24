[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
  [Parameter(Mandatory = $true)][string[]]$ArtifactPath,
  [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
  throw "Release-manifest output directory was not found: $outputDirectory"
}

$artifacts = foreach ($itemPath in $ArtifactPath) {
  $resolvedPath = [IO.Path]::GetFullPath($itemPath)
  if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
    throw "Release artifact was not found: $resolvedPath"
  }
  $item = Get-Item -LiteralPath $resolvedPath
  $signature = if ($item.Extension -in @('.exe', '.dll')) {
    Get-AuthenticodeSignature -LiteralPath $resolvedPath
  } else { $null }
  [ordered]@{
    file = $item.Name
    bytes = $item.Length
    sha256 = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    authenticode = if ($signature) { "$($signature.Status)" } else { 'NotApplicable' }
    publisher = if ($signature?.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
  }
}

$sourceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$safeSourceRoot = $sourceRoot.Replace('\', '/')
$sourceCommit = $null
$sourceDirty = $null
try {
  $commitOutput = @(& git -c "safe.directory=$safeSourceRoot" -C $sourceRoot rev-parse HEAD 2>$null)
  $gitExitCode = $LASTEXITCODE
  $sourceCommit = ($commitOutput | Select-Object -First 1).Trim()
  if ($gitExitCode -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') { $sourceCommit = $null }
} catch { $sourceCommit = $null }
try {
  $sourceStatus = @(& git -c "safe.directory=$safeSourceRoot" -C $sourceRoot status --porcelain 2>$null)
  $gitExitCode = $LASTEXITCODE
  if ($gitExitCode -eq 0) { $sourceDirty = $sourceStatus.Count -gt 0 }
} catch { $sourceDirty = $null }

$manifest = [ordered]@{
  schemaVersion = 1
  product = 'Codex Wallpaper Skin'
  version = $Version
  generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
  sourceCommit = $sourceCommit
  sourceDirty = $sourceDirty
  platform = 'Windows 11 x64'
  runtimeSelfContained = $true
  developerToolsRequired = $false
  wallpaperEngineRequired = $false
  artifacts = @($artifacts)
}

$temporaryPath = "$OutputPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
  $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporaryPath -Encoding utf8
  Move-Item -LiteralPath $temporaryPath -Destination $OutputPath -Force
} finally {
  if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
}

$manifestHash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestLeaf = Split-Path -Leaf $OutputPath
Set-Content -LiteralPath "$OutputPath.sha256" -Value "$manifestHash  $manifestLeaf" -Encoding ascii
Write-Output "Release manifest: $OutputPath"
