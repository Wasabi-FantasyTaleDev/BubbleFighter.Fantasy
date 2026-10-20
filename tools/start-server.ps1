# Starts the Bubble Fighter Fantasy host with the kr-1256 profile.
# Loads repo-root .env when present. Values are never printed.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Usage {
    Write-Host @'
Start the Bubble Fighter Fantasy host (profile kr-1256).

Usage:
  tools\start-server.cmd [options] [host-args...]
  tools\start-server.ps1 [options] [host-args...]

Options consumed by this script:
  --skip-build   skip dotnet build (fails if the Release host is missing)
  --rebuild      force a non-incremental Release build
  -h, --help     show this text

Other arguments are forwarded to the host after:
  run --profile kr-1256

Examples:
  tools\start-server.cmd
  tools\start-server.cmd --dump-config
  tools\start-server.cmd --log-level Debug

Environment:
  If .env exists at the repository root, KEY=VALUE lines are loaded into this
  process. Non-empty variables already set in the process are left unchanged.
  File contents and values are not printed.

No-database login test:
  Copy profiles\kr-1256\local.patch.yaml.example to
  profiles\kr-1256\local.patch.yaml (git-ignored) to disable persistence.mysql.
'@
}

function Import-DotEnv {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }

    # Default UTF-8 reader strips a BOM so the first key is not corrupted.
    $lines = [System.IO.File]::ReadAllLines($Path)
    foreach ($line in $lines) {
        if ($null -eq $line) {
            continue
        }

        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) {
            continue
        }

        if ($trimmed.Length -ge 7 -and $trimmed.Substring(0, 7).Equals('export ', [StringComparison]::OrdinalIgnoreCase)) {
            $trimmed = $trimmed.Substring(7).Trim()
        }

        $eq = $trimmed.IndexOf('=')
        if ($eq -lt 1) {
            continue
        }

        $name = $trimmed.Substring(0, $eq).Trim()
        if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
            continue
        }

        $value = $trimmed.Substring($eq + 1).Trim()
        if ($value.Length -ge 2) {
            $quote = $value[0]
            if (($quote -eq [char]'"' -or $quote -eq [char]"'") -and $value[$value.Length - 1] -eq $quote) {
                $value = $value.Substring(1, $value.Length - 2)
            }
        }

        $existing = [Environment]::GetEnvironmentVariable($name, 'Process')
        if (-not [string]::IsNullOrEmpty($existing)) {
            continue
        }

        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }

    return $true
}

function Test-HostAssemblyPresent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$HostDll
    )

    return (Test-Path -LiteralPath $HostDll)
}

$skipBuild = $false
$rebuild = $false
$hostArgs = New-Object System.Collections.Generic.List[string]

foreach ($arg in $args) {
    switch -CaseSensitive ($arg) {
        '--skip-build' { $skipBuild = $true }
        '--rebuild' { $rebuild = $true }
        '-h' { Show-Usage; exit 0 }
        '--help' { Show-Usage; exit 0 }
        default { [void]$hostArgs.Add([string]$arg) }
    }
}

if ($skipBuild -and $rebuild) {
    Write-Error 'Use only one of --skip-build and --rebuild.'
    exit 1
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $repoRoot 'BubbleFighter.Fantasy.sln'
$hostProject = Join-Path $repoRoot 'src\BubbleFighter.Fantasy.Host\BubbleFighter.Fantasy.Host.csproj'
$hostDll = Join-Path $repoRoot 'src\BubbleFighter.Fantasy.Host\bin\Release\net10.0\BubbleFighter.Fantasy.Host.dll'
$envFile = Join-Path $repoRoot '.env'
$localPatch = Join-Path $repoRoot 'profiles\kr-1256\local.patch.yaml'
$localPatchExample = Join-Path $repoRoot 'profiles\kr-1256\local.patch.yaml.example'

if (-not (Test-Path -LiteralPath $solution)) {
    Write-Error "Solution not found: $solution"
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet is not on PATH. Install .NET SDK 10.0+.'
    exit 1
}

Set-Location -LiteralPath $repoRoot

if (Import-DotEnv -Path $envFile) {
    Write-Host "Loaded environment from $envFile"
}
else {
    Write-Host 'No .env file at the repository root; using the current process environment.'
}

$mysqlPassword = [Environment]::GetEnvironmentVariable('BFF_MYSQL_PASSWORD', 'Process')
if ([string]::IsNullOrEmpty($mysqlPassword) -and -not (Test-Path -LiteralPath $localPatch)) {
    Write-Warning @"
BFF_MYSQL_PASSWORD is empty and profiles\kr-1256\local.patch.yaml is missing.
The persistence.mysql row will fail to load. For a no-DB login test, copy
$localPatchExample
to
$localPatch
"@
}

if ($skipBuild) {
    if (-not (Test-HostAssemblyPresent -HostDll $hostDll)) {
        Write-Error "Release host is missing at $hostDll. Run without --skip-build."
        exit 1
    }

    Write-Host 'Skipping build (--skip-build).'
}
else {
    $buildArgs = @('build', $solution, '-c', 'Release')
    if ($rebuild) {
        $buildArgs += '--no-incremental'
        Write-Host 'Building BubbleFighter.Fantasy.sln (Release, --no-incremental)...'
    }
    else {
        Write-Host 'Building BubbleFighter.Fantasy.sln (Release, incremental if up to date)...'
    }

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$runArgs = @(
    'run',
    '--project', $hostProject,
    '-c', 'Release',
    '--no-build',
    '--',
    'run',
    '--profile', 'kr-1256'
)
foreach ($hostArg in $hostArgs) {
    $runArgs += $hostArg
}

if ($hostArgs -contains '--dump-config') {
    Write-Warning 'Host --dump-config prints interpolated config and may include secrets. Do not share that output.'
}

Write-Host 'Starting host: run --profile kr-1256'
& dotnet @runArgs
exit $LASTEXITCODE
