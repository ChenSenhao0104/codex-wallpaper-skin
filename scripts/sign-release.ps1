[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string[]]$Path,
  [Parameter(Mandatory = $true)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$CertificateThumbprint,
  [Parameter(Mandatory = $true)][ValidatePattern('^https://')][string]$TimestampUrl,
  [string]$SignToolPath
)

$ErrorActionPreference = 'Stop'

function Resolve-SignTool {
  param([string]$ExplicitPath)
  if ($ExplicitPath) {
    $resolved = [IO.Path]::GetFullPath($ExplicitPath)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
      throw "SignTool was not found: $resolved"
    }
    return $resolved
  }

  $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
  if ($command) { return $command.Source }

  $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
  if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
    $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory -ErrorAction Stop |
      Sort-Object Name -Descending |
      ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
      Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
      Select-Object -First 1
    if ($candidate) { return $candidate }
  }
  throw 'signtool.exe was not found. Install the Windows SDK or pass -SignToolPath.'
}

$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction SilentlyContinue
if (-not $certificate -or -not $certificate.HasPrivateKey) {
  throw 'The requested CurrentUser code-signing certificate with a private key was not found.'
}

$signTool = Resolve-SignTool -ExplicitPath $SignToolPath
foreach ($itemPath in $Path) {
  $resolvedPath = [IO.Path]::GetFullPath($itemPath)
  if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
    throw "Signing input was not found: $resolvedPath"
  }
  if ([IO.Path]::GetExtension($resolvedPath) -notin @('.exe', '.dll')) {
    throw "Only executable PE files can be signed by this release helper: $resolvedPath"
  }

  & $signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /v $resolvedPath
  if ($LASTEXITCODE -ne 0) { throw "SignTool failed for $resolvedPath." }
  $signature = Get-AuthenticodeSignature -LiteralPath $resolvedPath
  if ($signature.Status -ne 'Valid') {
    throw "Authenticode verification failed for ${resolvedPath}: $($signature.StatusMessage)"
  }
  Write-Output "Signed and verified: $resolvedPath"
}
