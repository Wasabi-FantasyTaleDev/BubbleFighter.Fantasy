# Refresh the complete runnable deployment when its source revision is stale.
[CmdletBinding()]
param([switch]$ForceRebuild, [switch]$SkipBuild)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($ForceRebuild -and $SkipBuild) { throw 'ForceRebuild and SkipBuild cannot be combined.' }
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$deployRoot = Join-Path $repoRoot 'artifacts/deploy'
$current = Join-Path $deployRoot 'current'
$buildOutput = Join-Path $repoRoot 'src/BubbleFighter.Fantasy.Host/bin/Release/net10.0'
$hostName = 'BubbleFighter.Fantasy.Host.exe'
$sourcePaths = @('src', 'data', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'BubbleFighter.Fantasy.sln')
$utf8 = New-Object System.Text.UTF8Encoding($false)
Push-Location -LiteralPath $repoRoot
$lock = $null
try {
    $revision = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the source revision.' }
    $sourceStatus = (& git status --porcelain --untracked-files=normal -- @sourcePaths | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine whether source files have changed.' }
    New-Item -ItemType Directory -Path $deployRoot -Force | Out-Null
    $lock = [IO.File]::Open((Join-Path $deployRoot '.update.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $stampPath = Join-Path $current 'SOURCE_COMMIT.txt'
    $stamp = if (Test-Path -LiteralPath $stampPath) { [IO.File]::ReadAllText($stampPath).Trim() } else { '' }
    $dirtyStamp = Join-Path $current 'SOURCE_DIRTY.txt'
    if (-not $ForceRebuild -and $sourceStatus.Length -eq 0 -and $stamp -eq $revision `
        -and -not (Test-Path -LiteralPath $dirtyStamp) `
        -and (Test-Path -LiteralPath (Join-Path $current $hostName)) `
        -and (Test-Path -LiteralPath (Join-Path $current 'plugins/BubbleFighter.Fantasy.Lobby/BubbleFighter.Fantasy.Lobby.dll'))) {
        Write-Host "Deployment is current: $revision"
        return
    }

    # Refuse replacement while that exact deployment is in use; other instances are unrelated.
    $prefix = [IO.Path]::GetFullPath($current).TrimEnd('\') + '\'
    $running = @(Get-CimInstance Win32_Process | Where-Object {
        ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) `
        -or ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -and $_.CommandLine.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0)
    })
    if ($running.Count -gt 0) {
        throw "The current deployment is running (PID $($running.ProcessId -join ', ')). Stop that instance before refreshing; no process was terminated."
    }
    Write-Host "Refreshing deployment from '$stamp' to '$revision'."
    if (-not $SkipBuild) {
        & dotnet build BubbleFighter.Fantasy.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Release build failed; the previous deployment is unchanged.' }
    }
    $hostDll = Join-Path $buildOutput 'BubbleFighter.Fantasy.Host.dll'
    if (-not (Test-Path -LiteralPath $hostDll)) { throw 'The Release host is missing.' }
    $builtVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($hostDll).ProductVersion
    if ($builtVersion -notlike "*+$revision*") {
        throw "The built host is from a different revision ($builtVersion). Build the current revision first."
    }
    $afterRevision = (& git rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $afterRevision -ne $revision) { throw 'HEAD changed during deployment; retry after the other writer finishes.' }
    if ($SkipBuild -and $sourceStatus.Length -gt 0) { throw 'SkipBuild is not allowed with uncommitted source changes.' }

    $suffix = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $stage = Join-Path $deployRoot ('staging-' + $suffix)
    $previous = Join-Path $deployRoot ('previous-' + $suffix)
    New-Item -ItemType Directory -Path $stage | Out-Null
    Get-ChildItem -LiteralPath $buildOutput -Force | Copy-Item -Destination $stage -Recurse -Force
    $binaries = @(Get-ChildItem -LiteralPath $buildOutput -Recurse -File | Where-Object { $_.Extension -in '.dll', '.exe' })
    foreach ($file in $binaries) {
        $relative = $file.FullName.Substring($buildOutput.Length).TrimStart('\', '/')
        $copy = Join-Path $stage $relative
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) {
            throw "Deployment copy verification failed: $relative"
        }
    }
    [IO.File]::WriteAllText((Join-Path $stage 'SOURCE_COMMIT.txt'), $revision + [Environment]::NewLine, $utf8)
    if ($sourceStatus.Length -gt 0) {
        [IO.File]::WriteAllText((Join-Path $stage 'SOURCE_DIRTY.txt'), $sourceStatus, $utf8)
    }
    # Both paths are fixed children of artifacts/deploy. Preserve the old directory for rollback.
    $hadPrevious = Test-Path -LiteralPath $current
    if ($hadPrevious) { Move-Item -LiteralPath $current -Destination $previous }
    try { Move-Item -LiteralPath $stage -Destination $current }
    catch {
        if ($hadPrevious -and -not (Test-Path -LiteralPath $current)) {
            Move-Item -LiteralPath $previous -Destination $current
        }
        throw
    }
    Write-Host "Deployment ready: $current ($($binaries.Count) verified binaries)."
    if ($hadPrevious) { Write-Host "Previous deployment retained: $previous" }
}
finally {
    if ($null -ne $lock) { $lock.Dispose() }
    Pop-Location
}
