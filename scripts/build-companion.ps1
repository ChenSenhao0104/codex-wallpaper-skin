[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
  [switch]$Publish,
  [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$PublishDirectoryName = 'win-x64',
  [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$')][string]$ArchiveBaseName = 'CodexWallpaperSkin-win-x64'
)

$ErrorActionPreference = 'Stop'
$skillRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $skillRoot 'companion\CodexWallpaperSkin.Companion.csproj'
$targetFramework = 'net8.0-windows10.0.19041.0'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
  throw "Companion project not found: $project"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) { throw '.NET 8 SDK is required.' }
$versionText = (& $dotnet.Source --version 2>$null | Select-Object -First 1).Trim()
$major = 0
if (-not [int]::TryParse(($versionText -split '\.')[0], [ref]$major) -or $major -lt 8) {
  throw ".NET 8 SDK or newer is required; found '$versionText'."
}
if ($Publish) { $Configuration = 'Release' }

$sourceCommit = $null
if ($Publish) {
  try {
    $candidateCommit = (& git -C $skillRoot rev-parse HEAD 2>$null | Select-Object -First 1).Trim()
    if ($LASTEXITCODE -eq 0 -and $candidateCommit -match '^[0-9a-f]{40}$') {
      $sourceCommit = $candidateCommit
    }
  }
  catch { $sourceCommit = $null }
}

function Remove-GeneratedDirectory {
  param(
    [Parameter(Mandatory = $true)][string]$Target,
    [Parameter(Mandatory = $true)][string]$ExpectedParent,
    [Parameter(Mandatory = $true)][string]$ExpectedLeaf
  )
  $resolvedTarget = [IO.Path]::GetFullPath($Target).TrimEnd('\')
  $resolvedParent = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\')
  if ([IO.Path]::GetDirectoryName($resolvedTarget).TrimEnd('\') -cne $resolvedParent -or
      [IO.Path]::GetFileName($resolvedTarget) -cne $ExpectedLeaf) {
    throw "Refusing to replace an unexpected generated directory: $resolvedTarget"
  }
  if (-not (Test-Path -LiteralPath $resolvedTarget)) { return }
  $item = Get-Item -LiteralPath $resolvedTarget -Force
  if (-not $item.PSIsContainer -or
      ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.LinkType) {
    throw "Refusing to replace a non-directory or reparse point: $resolvedTarget"
  }
  $nestedLink = Get-ChildItem -LiteralPath $resolvedTarget -Force -Recurse -ErrorAction Stop |
    Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.LinkType } |
    Select-Object -First 1
  if ($nestedLink) {
    throw "Refusing to replace generated output containing a reparse point: $($nestedLink.FullName)"
  }
  try {
    Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
  }
  catch {
    $removeError = $_
    # Editors and file watchers can keep a handle to the generated directory
    # after every child has been removed. Reusing that verified empty directory
    # is safe and avoids requiring callers to force-close unrelated programs.
    $remaining = @(Get-ChildItem -LiteralPath $resolvedTarget -Force -ErrorAction Stop)
    if ($remaining.Count -ne 0) { throw $removeError }
  }
}

function Remove-GeneratedFile {
  param(
    [Parameter(Mandatory = $true)][string]$Target,
    [Parameter(Mandatory = $true)][string]$ExpectedParent
  )
  $resolvedTarget = [IO.Path]::GetFullPath($Target)
  $resolvedParent = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\')
  if ([IO.Path]::GetDirectoryName($resolvedTarget).TrimEnd('\') -cne $resolvedParent) {
    throw "Refusing to replace an unexpected generated file: $resolvedTarget"
  }
  if (-not (Test-Path -LiteralPath $resolvedTarget)) { return }
  $item = Get-Item -LiteralPath $resolvedTarget -Force
  if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.LinkType) {
    throw "Refusing to replace a directory or reparse point as a generated file: $resolvedTarget"
  }
  Remove-Item -LiteralPath $resolvedTarget -Force
}

function Get-RuntimePackVersion {
  param(
    [Parameter(Mandatory = $true)][psobject]$DependencyManifest,
    [Parameter(Mandatory = $true)][string]$PackName
  )
  $prefix = "runtimepack.$PackName/"
  $matches = @($DependencyManifest.libraries.PSObject.Properties |
    Where-Object { $_.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
  if ($matches.Count -ne 1) {
    throw "Expected exactly one resolved $PackName runtime pack in the Release dependency manifest; found $($matches.Count)."
  }
  return $matches[0].Name.Substring($prefix.Length)
}

function Resolve-RuntimePackFile {
  param(
    [Parameter(Mandatory = $true)][string[]]$PackageFolders,
    [Parameter(Mandatory = $true)][string]$PackageId,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string[]]$CandidateNames
  )
  foreach ($packageFolder in $PackageFolders) {
    $packageRoot = Join-Path (Join-Path $packageFolder $PackageId.ToLowerInvariant()) $Version
    foreach ($candidateName in $CandidateNames) {
      $candidate = Join-Path $packageRoot $candidateName
      if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $candidate
      }
    }
  }
  throw "Could not find $($CandidateNames -join ' or ') for resolved runtime pack $PackageId $Version."
}

$distRoot = $null
$publishDirectory = $null
$portableArchive = $null
$legacySkillArchive = $null
$runtimeLicenseAssets = @()
if ($Publish) {
  $distRoot = [IO.Path]::GetFullPath((Join-Path $skillRoot 'dist')).TrimEnd('\')
  if (Test-Path -LiteralPath $distRoot) {
    $distItem = Get-Item -LiteralPath $distRoot -Force
    if (-not $distItem.PSIsContainer -or
        ($distItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $distItem.LinkType) {
      throw "Refusing to publish through a non-directory or reparse-point dist path: $distRoot"
    }
  }
  if ($PublishDirectoryName -in @('.', '..')) {
    throw 'PublishDirectoryName must be a safe directory leaf.'
  }
  $publishDirectory = Join-Path $distRoot $PublishDirectoryName
  $portableArchive = Join-Path $distRoot "$ArchiveBaseName.zip"
  $legacySkillArchive = Join-Path $distRoot 'CodexWallpaperSkin-skill-win-x64.zip'
  Remove-GeneratedDirectory -Target $publishDirectory -ExpectedParent $distRoot -ExpectedLeaf $PublishDirectoryName
  foreach ($artifact in @($portableArchive, "$portableArchive.sha256", $legacySkillArchive, "$legacySkillArchive.sha256")) {
    Remove-GeneratedFile -Target $artifact -ExpectedParent $distRoot
  }

  # Remove prior deliverables before any release preflight. A failed release must
  # never leave an older ZIP that looks like the result of the current command.
  $node = Get-Command node -ErrorAction SilentlyContinue
  if ($null -eq $node) { throw 'Node.js is required for the release runtime smoke test.' }
  foreach ($document in @('PORTABLE-README.md', 'PORTABLE-README.en.md', 'usage-process.md', 'LICENSE', 'SECURITY.md', 'THIRD_PARTY_NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $skillRoot $document) -PathType Leaf)) {
      throw "Required release document was not found: $document"
    }
  }
  if (-not (Test-Path -LiteralPath (Join-Path $skillRoot 'companion\ThirdParty\we-scene\LICENSE') -PathType Leaf)) {
    throw 'Required we-scene MIT license was not found.'
  }
}

& $dotnet.Source restore $project --nologo --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

$buildArguments = @('build', $project, '--configuration', $Configuration, '--no-restore', '--nologo')
if ($Publish) {
  # A release build must not reuse a DLL compiled in another checkout. Rebuilding
  # also keeps AssemblyInformationalVersion aligned with the current Git commit.
  $buildArguments += '--no-incremental'
  $buildArguments += '-p:DebugType=None'
  $buildArguments += '-p:DebugSymbols=false'
  $buildArguments += '-p:ContinuousIntegrationBuild=true'
  if ($sourceCommit) { $buildArguments += "-p:SourceRevisionId=$sourceCommit" }
}
& $dotnet.Source @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

if ($Publish) {
  try {
  $releaseAssembly = Join-Path $skillRoot "companion\bin\Release\$targetFramework\win-x64\CodexWallpaperSkin.dll"
  & $dotnet.Source $releaseAssembly --self-test
  if ($LASTEXITCODE -ne 0) { throw 'Release companion self-test failed.' }
  & $node.Source (Join-Path $PSScriptRoot 'runtime-smoke-test.mjs')
  if ($LASTEXITCODE -ne 0) { throw 'Renderer runtime smoke test failed.' }

  $publishArguments = @(
    'publish', $project, '--configuration', 'Release', '--runtime', 'win-x64',
    '--self-contained', 'true', '--no-restore', '--nologo', '--no-build',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=None', '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true',
    '--output', $publishDirectory
  )
  if ($sourceCommit) { $publishArguments += "-p:SourceRevisionId=$sourceCommit" }
  & $dotnet.Source @publishArguments
  if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

  # The self-contained payload comes from NuGet runtime packs, which can differ
  # from the installed SDK. Copy the notices from the exact versions resolved
  # into this build rather than from the dotnet installation root.
  $releaseDependencyPath = Join-Path $skillRoot "companion\bin\Release\$targetFramework\win-x64\CodexWallpaperSkin.deps.json"
  $projectAssetsPath = Join-Path $skillRoot 'companion\obj\project.assets.json'
  $releaseDependencies = Get-Content -LiteralPath $releaseDependencyPath -Raw | ConvertFrom-Json
  $projectAssets = Get-Content -LiteralPath $projectAssetsPath -Raw | ConvertFrom-Json
  $packageFolders = @($projectAssets.packageFolders.PSObject.Properties | ForEach-Object { $_.Name })
  if ($packageFolders.Count -eq 0) { throw 'The restore manifest exposed no NuGet package folders.' }
  $corePackId = 'Microsoft.NETCore.App.Runtime.win-x64'
  $desktopPackId = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'
  $corePackVersion = Get-RuntimePackVersion -DependencyManifest $releaseDependencies -PackName $corePackId
  $desktopPackVersion = Get-RuntimePackVersion -DependencyManifest $releaseDependencies -PackName $desktopPackId
  $runtimeLicenseAssets = @(
    [ordered]@{
      source = Resolve-RuntimePackFile -PackageFolders $packageFolders -PackageId $corePackId -Version $corePackVersion -CandidateNames @('LICENSE.TXT', 'LICENSE')
      destination = 'DOTNET-RUNTIME-LICENSE.txt'
    },
    [ordered]@{
      source = Resolve-RuntimePackFile -PackageFolders $packageFolders -PackageId $corePackId -Version $corePackVersion -CandidateNames @('THIRD-PARTY-NOTICES.TXT', 'THIRD-PARTY-NOTICES')
      destination = 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt'
    },
    [ordered]@{
      source = Resolve-RuntimePackFile -PackageFolders $packageFolders -PackageId $desktopPackId -Version $desktopPackVersion -CandidateNames @('LICENSE.TXT', 'LICENSE')
      destination = 'DOTNET-WINDOWSDESKTOP-LICENSE.txt'
    }
  )

  Copy-Item -LiteralPath (Join-Path $skillRoot 'PORTABLE-README.md') -Destination (Join-Path $publishDirectory 'README.md') -Force
  Copy-Item -LiteralPath (Join-Path $skillRoot 'PORTABLE-README.en.md') -Destination (Join-Path $publishDirectory 'README.en.md') -Force
  foreach ($document in @('usage-process.md', 'LICENSE', 'SECURITY.md', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $skillRoot $document) -Destination $publishDirectory -Force
  }
  Copy-Item -LiteralPath (Join-Path $skillRoot 'companion\ThirdParty\we-scene\LICENSE') `
    -Destination (Join-Path $publishDirectory 'WE-SCENE-LICENSE.txt') -Force
  foreach ($asset in $runtimeLicenseAssets) {
    Copy-Item -LiteralPath $asset.source -Destination (Join-Path $publishDirectory $asset.destination) -Force
  }
  Set-Content -LiteralPath (Join-Path $publishDirectory 'DOTNET-RUNTIME-PACKS.txt') -Encoding ascii -Value @(
    "$corePackId $corePackVersion",
    "$desktopPackId $desktopPackVersion"
  )

  $publishedExecutable = Join-Path $publishDirectory 'CodexWallpaperSkin.exe'
  $publishedVersion = (Get-Item -LiteralPath $publishedExecutable).VersionInfo.ProductVersion
  if ($sourceCommit -and -not $publishedVersion.EndsWith("+$sourceCommit", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Published executable source revision '$publishedVersion' does not match HEAD $sourceCommit."
  }

  # Prevent release binaries from exposing the build checkout or Windows user
  # profile through an embedded PDB path or other compiler metadata.
  $binaryText = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($publishedExecutable))
  $forbiddenBuildPaths = @(
    $skillRoot,
    [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
  ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
  foreach ($forbiddenPath in $forbiddenBuildPaths) {
    if ($binaryText.Contains($forbiddenPath, [StringComparison]::OrdinalIgnoreCase) -or
        $binaryText.Contains($forbiddenPath.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
      throw "Published executable contains a local build path. Release output was rejected."
    }
  }
  $binaryText = $null

  $selfTestProcess = Start-Process -FilePath $publishedExecutable -ArgumentList '--self-test' -PassThru -WindowStyle Hidden
  if (-not $selfTestProcess.WaitForExit(60000)) {
    try {
      $selfTestProcess.Kill()
      [void]$selfTestProcess.WaitForExit(5000)
    } catch { }
    throw 'Published companion self-test exceeded the 60-second safety timeout.'
  }
  $selfTestProcess.Refresh()
  if ($selfTestProcess.ExitCode -ne 0) {
    throw "Published companion self-test failed with exit code $($selfTestProcess.ExitCode)."
  }

  Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal
  $portableChecksum = (Get-FileHash -LiteralPath $portableArchive -Algorithm SHA256).Hash.ToLowerInvariant()
  Set-Content -LiteralPath "$portableArchive.sha256" -Value "$portableChecksum  $(Split-Path -Leaf $portableArchive)" -Encoding ascii

  Write-Host "Published portable build: $publishDirectory"
  Write-Host "Portable release: $portableArchive"
  }
  catch {
    $publishError = $_
    $cleanupFailures = @()
    foreach ($artifact in @($portableArchive, "$portableArchive.sha256")) {
      try { Remove-GeneratedFile -Target $artifact -ExpectedParent $distRoot }
      catch { $cleanupFailures += $_.Exception.Message }
    }
    try { Remove-GeneratedDirectory -Target $publishDirectory -ExpectedParent $distRoot -ExpectedLeaf $PublishDirectoryName }
    catch { $cleanupFailures += $_.Exception.Message }
    if ($cleanupFailures.Count -gt 0) {
      $message = "Publish failed: $($publishError.Exception.Message) Cleanup also failed: $($cleanupFailures -join ' | ')"
      throw [InvalidOperationException]::new($message, $publishError.Exception)
    }
    throw $publishError
  }
}
