[CmdletBinding()]
param(
  [switch]$Json
)

$ErrorActionPreference = 'Stop'
$script:StrictUtf8Encoding = [System.Text.UTF8Encoding]::new($false, $true)

function Read-Utf8TextFile {
  param([Parameter(Mandatory = $true)][string]$Path)
  return [IO.File]::ReadAllText($Path, $script:StrictUtf8Encoding)
}

function Get-CommandVersion {
  param([Parameter(Mandatory = $true)][string]$Name, [string[]]$Arguments = @('--version'))
  $command = Get-Command $Name -ErrorAction SilentlyContinue
  if ($null -eq $command) { return $null }

  $process = [Diagnostics.Process]::new()
  $started = $false
  try {
    $process.StartInfo = [Diagnostics.ProcessStartInfo]@{
      FileName = $command.Source
      Arguments = ($Arguments -join ' ')
      UseShellExecute = $false
      CreateNoWindow = $true
      RedirectStandardOutput = $true
      RedirectStandardError = $true
    }
    $started = $process.Start()
    if (-not $started) { return $null }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(3000)) {
      try { $process.Kill() } catch { }
      try { [void]$process.WaitForExit(1000) } catch { }
      return $null
    }

    if (-not $stdoutTask.Wait(1000)) { return $null }
    [void]$stderrTask.Wait(1000)
    $output = "$($stdoutTask.Result)".Trim()
    if ([string]::IsNullOrWhiteSpace($output) -and $stderrTask.IsCompleted) {
      $output = "$($stderrTask.Result)".Trim()
    }
    if ([string]::IsNullOrWhiteSpace($output)) { return $null }
    return @($output -split "`r?`n")[0].Trim()
  } catch {
    return $null
  } finally {
    if ($started) {
      try {
        if (-not $process.HasExited) {
          # Kill only the process started for this version probe. Never search
          # for or terminate other dotnet/node processes on the machine.
          $process.Kill()
          [void]$process.WaitForExit(1000)
        }
      } catch { }
    }
    $process.Dispose()
  }
}

function Get-CodexPackages {
  try {
    return @(Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop |
      Where-Object {
        $_.Name -ieq 'OpenAI.Codex' -and
        $_.PackageFamilyName -ieq 'OpenAI.Codex_2p2nqsd0c76g0'
      } |
      ForEach-Object {
      [ordered]@{
        name = $_.Name
        version = "$($_.Version)"
        architecture = "$($_.Architecture)"
        packageFamilyName = $_.PackageFamilyName
        installLocation = $_.InstallLocation
        signatureKind = "$($_.SignatureKind)"
      }
    })
  } catch {
    return @()
  }
}

function Test-OfficialCodexExecutablePath {
  param(
    [Parameter(Mandatory = $true)][string]$Path,
    [AllowEmptyCollection()][string[]]$PackageInstallLocations = @()
  )

  try {
    $fullPath = [IO.Path]::GetFullPath($Path)
    foreach ($location in $PackageInstallLocations) {
      if ([string]::IsNullOrWhiteSpace($location)) { continue }
      $packageRoot = [IO.Path]::GetFullPath($location).TrimEnd('\') + '\'
      if (-not $fullPath.StartsWith($packageRoot, [StringComparison]::OrdinalIgnoreCase)) { continue }
      $relativePath = $fullPath.Substring($packageRoot.Length)
      return ($relativePath -ieq 'app\ChatGPT.exe' -or $relativePath -ieq 'app\Codex.exe')
    }

    # Package enumeration can be restricted. The fallback still requires the
    # canonical WindowsApps root, official publisher ID, and expected app path.
    $programFilesRoot = if (-not [string]::IsNullOrWhiteSpace($env:ProgramW6432)) {
      $env:ProgramW6432
    } else {
      [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    }
    $windowsAppsRoot = [IO.Path]::GetFullPath((Join-Path $programFilesRoot 'WindowsApps')).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($windowsAppsRoot, [StringComparison]::OrdinalIgnoreCase)) { return $false }
    $relativePath = $fullPath.Substring($windowsAppsRoot.Length)
    return $relativePath -imatch '^OpenAI\.Codex_[^\\]+_x64__2p2nqsd0c76g0\\app\\(?:ChatGPT|Codex)\.exe$'
  } catch {
    return $false
  }
}

function Get-RunningCodexReport {
  param([AllowEmptyCollection()][string[]]$PackageInstallLocations = @())

  $verifiedPaths = @()
  $unverifiedCount = 0
  foreach ($process in @(Get-Process -Name 'ChatGPT', 'Codex' -ErrorAction SilentlyContinue)) {
    try {
      $path = $process.Path
      if ($path -and (Test-OfficialCodexExecutablePath -Path $path -PackageInstallLocations $PackageInstallLocations)) {
        $verifiedPaths += $path
      } else {
        $unverifiedCount++
      }
    } catch {
      # An inaccessible or pathless same-name process is never treated as Codex.
      $unverifiedCount++
    }
  }
  return [ordered]@{
    verifiedPaths = @($verifiedPaths | Sort-Object -Unique)
    unverifiedSameNameProcessCount = $unverifiedCount
  }
}

function Add-ExistingPath {
  param(
    [Parameter(Mandatory = $true)][System.Collections.Generic.HashSet[string]]$Set,
    [AllowNull()][string]$Path,
    [Parameter(Mandatory = $true)][int]$MaxCount,
    [Parameter(Mandatory = $true)][ref]$Truncated
  )
  if ([string]::IsNullOrWhiteSpace($Path)) { return }
  try {
    if (Test-Path -LiteralPath $Path -PathType Container) {
      $resolved = (Resolve-Path -LiteralPath $Path).Path
      if ($Set.Contains($resolved)) { return }
      if ($Set.Count -ge $MaxCount) {
        $Truncated.Value = $true
        return
      }
      [void]$Set.Add($resolved)
    }
  } catch {
    # A stale Steam library entry is not fatal to diagnostics.
  }
}

function Get-SteamLibraries {
  $maxRoots = 16
  $truncated = $false
  $roots = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
  $registryPaths = @(
    'HKCU:\Software\Valve\Steam',
    'HKLM:\Software\Valve\Steam',
    'HKLM:\Software\WOW6432Node\Valve\Steam'
  )
  foreach ($registryPath in $registryPaths) {
    try {
      $value = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop
      Add-ExistingPath -Set $roots -Path $value.SteamPath -MaxCount $maxRoots -Truncated ([ref]$truncated)
      Add-ExistingPath -Set $roots -Path $value.InstallPath -MaxCount $maxRoots -Truncated ([ref]$truncated)
    } catch {
      # Registry location is optional.
    }
  }

  foreach ($root in @($roots)) {
    $libraryFile = Join-Path $root 'steamapps\libraryfolders.vdf'
    if (-not (Test-Path -LiteralPath $libraryFile -PathType Leaf)) { continue }
    try {
      $item = Get-Item -LiteralPath $libraryFile
      if ($item.Length -gt 4MB) { continue }
      $text = Read-Utf8TextFile -Path $libraryFile
      foreach ($match in [regex]::Matches($text, '"path"\s+"(?<path>[^"]+)"')) {
        $candidate = $match.Groups['path'].Value -replace '\\\\', '\'
        Add-ExistingPath -Set $roots -Path $candidate -MaxCount $maxRoots -Truncated ([ref]$truncated)
      }
    } catch {
      # Malformed third-party configuration is reported as an absent library.
    }
  }
  return [ordered]@{
    libraries = @($roots | Sort-Object)
    scan = [ordered]@{
      maxRoots = $maxRoots
      discoveredRoots = $roots.Count
      truncated = $truncated
    }
  }
}

function Get-WallpaperEngineReport {
  param([Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$SteamLibraries)
  $maxProjects = 5000
  $budgetMilliseconds = 12000
  $projectsScanned = 0
  $projectLimitReached = $false
  $timeBudgetReached = $false
  $scanWatch = [Diagnostics.Stopwatch]::StartNew()
  $installs = @()
  $totals = [ordered]@{ image = 0; video = 0; scene = 0; web = 0; application = 0; unknown = 0 }
  foreach ($library in $SteamLibraries) {
    $installRoot = Join-Path $library 'steamapps\common\wallpaper_engine'
    $workshopRoot = Join-Path $library 'steamapps\workshop\content\431960'
    $executable = Join-Path $installRoot 'wallpaper64.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -and
        -not (Test-Path -LiteralPath $workshopRoot -PathType Container)) { continue }

    $counts = [ordered]@{ image = 0; video = 0; scene = 0; web = 0; application = 0; unknown = 0 }
    if (-not $projectLimitReached -and -not $timeBudgetReached -and
        (Test-Path -LiteralPath $workshopRoot -PathType Container)) {
      try {
        foreach ($directoryPath in [IO.Directory]::EnumerateDirectories($workshopRoot)) {
          if ($projectsScanned -ge $maxProjects) {
            $projectLimitReached = $true
            break
          }
          if ($scanWatch.ElapsedMilliseconds -ge $budgetMilliseconds) {
            $timeBudgetReached = $true
            break
          }

          # Count every workshop directory toward the global budget, including
          # malformed entries and reparse points, so hostile trees stay bounded.
          $projectsScanned++
          try {
            $attributes = [IO.File]::GetAttributes($directoryPath)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            $projectPath = Join-Path $directoryPath 'project.json'
            $projectFile = Get-Item -LiteralPath $projectPath -ErrorAction Stop
            if ($projectFile.Length -lt 1 -or $projectFile.Length -gt 1MB) { $counts.unknown++; continue }
            $projectText = Read-Utf8TextFile -Path $projectPath
            $project = $projectText | ConvertFrom-Json -ErrorAction Stop
            $type = "$($project.type)".ToLowerInvariant()
            if ($counts.Contains($type)) { $counts[$type]++ } else { $counts.unknown++ }
          } catch {
            $counts.unknown++
          }
        }
      } catch {
        # A library becoming inaccessible during enumeration is non-fatal.
      }
    }
    foreach ($key in @($totals.Keys)) { $totals[$key] += $counts[$key] }
    $installs += [ordered]@{
      root = $installRoot
      executable = if (Test-Path -LiteralPath $executable -PathType Leaf) { $executable } else { $null }
      workshopRoot = if (Test-Path -LiteralPath $workshopRoot -PathType Container) { $workshopRoot } else { $null }
      projects = $counts
    }
  }
  $scanWatch.Stop()
  $reasons = @()
  if ($projectLimitReached) { $reasons += 'projectLimit' }
  if ($timeBudgetReached) { $reasons += 'timeBudget' }
  return [ordered]@{
    installs = $installs
    totals = $totals
    scan = [ordered]@{
      projectsScanned = $projectsScanned
      maxProjects = $maxProjects
      elapsedMilliseconds = $scanWatch.ElapsedMilliseconds
      budgetMilliseconds = $budgetMilliseconds
      truncated = ($projectLimitReached -or $timeBudgetReached)
      reasons = $reasons
    }
  }
}

$steamLibraryResult = Get-SteamLibraries
$steamLibraries = @($steamLibraryResult.libraries)
$wallpaperEngine = Get-WallpaperEngineReport -SteamLibraries $steamLibraries
$codexPackages = @(Get-CodexPackages)
$compatibleCodexPackages = @($codexPackages | Where-Object { $_.architecture -ieq 'X64' })
$packageInstallLocations = @($codexPackages | ForEach-Object { $_.installLocation })
$runningCodex = Get-RunningCodexReport -PackageInstallLocations $packageInstallLocations
$runningCodexPaths = @($runningCodex.verifiedPaths)
$isWindowsPlatform = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
  [Runtime.InteropServices.OSPlatform]::Windows)
$osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$windowsBuild = [Environment]::OSVersion.Version.Build

$warnings = @()
if (-not $isWindowsPlatform) { $warnings += 'This release supports Windows only.' }
if ($osArchitecture -ne 'X64') { $warnings += 'This release requires x64 Windows; ARM64 and x86 are not supported.' }
if ($windowsBuild -lt 22000) { $warnings += 'This release requires Windows 11 build 22000 or newer.' }
if ($compatibleCodexPackages.Count -eq 0 -and $runningCodexPaths.Count -eq 0) { $warnings += 'The supported x64 OpenAI.Codex Store/MSIX app was not discovered.' }
if ($codexPackages.Count -gt $compatibleCodexPackages.Count) { $warnings += 'An unsupported non-x64 OpenAI.Codex package was ignored.' }
if ($runningCodex.unverifiedSameNameProcessCount -gt 0) {
  $warnings += "Ignored $($runningCodex.unverifiedSameNameProcessCount) ChatGPT/Codex process(es) whose executable path was not verified as the official OpenAI.Codex MSIX."
}
if ($wallpaperEngine.installs.Count -eq 0) { $warnings += 'Wallpaper Engine was not discovered; local image/video backgrounds remain available.' }
if ($steamLibraryResult.scan.truncated) {
  $warnings += "Steam library discovery was truncated at $($steamLibraryResult.scan.maxRoots) roots."
}
if ($wallpaperEngine.scan.truncated) {
  $reasonText = @($wallpaperEngine.scan.reasons) -join ', '
  $warnings += "Wallpaper workshop scanning was truncated ($reasonText) after $($wallpaperEngine.scan.projectsScanned) project directories."
}

$report = [ordered]@{
  schemaVersion = 1
  generatedAt = (Get-Date).ToUniversalTime().ToString('o')
  supported = ($isWindowsPlatform -and $osArchitecture -eq 'X64' -and $windowsBuild -ge 22000 -and
    ($compatibleCodexPackages.Count -gt 0 -or $runningCodexPaths.Count -gt 0))
  operatingSystem = [ordered]@{
    description = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    build = $windowsBuild
    architecture = $osArchitecture
    processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
  }
  runtimes = [ordered]@{
    powershell = "$($PSVersionTable.PSVersion)"
    dotnet = Get-CommandVersion -Name 'dotnet'
    node = Get-CommandVersion -Name 'node'
  }
  codex = [ordered]@{
    packages = $codexPackages
    runningExecutablePaths = $runningCodexPaths
    ignoredSameNameProcessCount = $runningCodex.unverifiedSameNameProcessCount
  }
  steamLibraries = $steamLibraries
  steamLibraryScan = $steamLibraryResult.scan
  wallpaperEngine = $wallpaperEngine
  warnings = $warnings
}

if ($Json) {
  $report | ConvertTo-Json -Depth 8
} else {
  Write-Host "Codex Wallpaper Skin doctor"
  Write-Host "  Supported baseline: $($report.supported)"
  Write-Host "  Windows build:      $windowsBuild"
  Write-Host "  Codex packages:     $($codexPackages.Count)"
  Write-Host "  Running Codex paths:$($runningCodexPaths.Count)"
  Write-Host "  Steam libraries:    $($steamLibraries.Count)"
  Write-Host "  Wallpaper projects: image=$($wallpaperEngine.totals.image), video=$($wallpaperEngine.totals.video), scene=$($wallpaperEngine.totals.scene), web=$($wallpaperEngine.totals.web)"
  Write-Host "  Projects scanned:   $($wallpaperEngine.scan.projectsScanned) (truncated=$($wallpaperEngine.scan.truncated))"
  foreach ($warning in $warnings) { Write-Warning $warning }
}
