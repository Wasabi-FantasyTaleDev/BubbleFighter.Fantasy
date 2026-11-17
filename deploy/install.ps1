<#
Install or upgrade the prebuilt Bubble Fighter Fantasy server on Windows (x64 or ARM64).

  irm https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.ps1 | iex
  & ([scriptblock]::Create((irm https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.ps1))) -NoPrompt -Start

Run without options in a console and it asks a few questions; any option given on the command line is not asked
again, and -NoPrompt (or a session without a console, such as an AI agent's) asks nothing and uses the defaults.

Layout under the install directory (C:\BubbleFighterFantasy by default; Windows refuses very long plugin paths):
  releases\<tag>\   one unpacked package per installed release
  current           junction to the active release
  state\            the setup assistant's saved installation; kept across upgrades
  dotnet\           the ASP.NET Core Runtime 10, only when the system has none
  settings.env      WebUI access, setup token and port chosen here; kept across upgrades
  bff.cmd           launcher; with no arguments it starts the server (the setup assistant on the first run)
#>
[CmdletBinding()]
param(
    [string]$Version = 'latest',
    [string]$Dir = '',
    [ValidateSet('', 'x64', 'arm64')][string]$Arch = '',
    [switch]$NoRuntime,
    [switch]$Service,
    [switch]$Start,
    [int]$AdminPort = 0,
    [switch]$RemoteAdmin,
    [switch]$LocalAdmin,
    [switch]$SetupToken,
    [switch]$NoSetupToken,
    [switch]$NoPrompt,
    [string]$From = '',
    [switch]$Help
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 draws a progress bar per downloaded block, which slows large downloads severely.
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repository = 'Wasabi-FantasyTaleDev/BubbleFighter.Fantasy'
$TaskName = 'BubbleFighterFantasy'

if ($Help) {
    @'
Usage: install.ps1 [options]

  -Version <tag>   release tag to install, e.g. v0.4.0 (default: the latest release)
  -Dir <path>      install directory (default: C:\BubbleFighterFantasy)
  -Arch <x64|arm64>  package to install (default: this processor)
  -NoRuntime       fail instead of downloading the ASP.NET Core Runtime 10 when it is missing
  -Service         as administrator: start the server with Windows (scheduled task BubbleFighterFantasy, SYSTEM)
  -Start           start the server in its own window when the installation finishes and open the setup page
  -AdminPort <n>   port of the setup assistant and administration WebUI (default 7300)
  -RemoteAdmin     let other machines open the WebUI (the default)
  -LocalAdmin      keep the WebUI on this machine only; open it from elsewhere through an SSH tunnel
  -SetupToken      require a generated token before the first-run setup wizard can be used (default: off)
  -NoSetupToken    do not require a setup token (the default)
  -NoPrompt        do not ask questions; use the options given and the defaults
  -From <dir>      install from a directory holding the release assets instead of downloading them
  -Help            show this help

Upgrading: run the script again. The state directory, settings.env and earlier releases are kept.
'@ | Write-Host
    return
}

function Fail([string]$Message) { throw "install.ps1: $Message" }
function Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function New-Secret {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

# Questions need a person at a console; an agent's shell has redirected input and gets the defaults.
$interactive = -not $NoPrompt -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
$noArguments = $PSBoundParameters.Count -eq 0
function Ask([string]$Question, [bool]$Default = $false) {
    if (-not $interactive) { return $Default }
    $hint = if ($Default) { '[Y/n]' } else { '[y/N]' }
    $answer = Read-Host "$Question $hint"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim() -match '^(y|yes)$'
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

# --- what to install --------------------------------------------------------------------------------
if (-not $Arch) {
    # A 32-bit PowerShell on a 64-bit Windows reports x86 in PROCESSOR_ARCHITECTURE and the real one here.
    $machine = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    switch ($machine) {
        'AMD64' { $Arch = 'x64' }
        'ARM64' { $Arch = 'arm64' }
        default { Fail "unsupported processor '$machine'; packages exist for x64 and ARM64." }
    }
}
if (-not $Dir) {
    $Dir = Join-Path $env:SystemDrive 'BubbleFighterFantasy'
    if ($noArguments -and $interactive) {
        $answer = Read-Host "Install folder [$Dir]"
        if (-not [string]::IsNullOrWhiteSpace($answer)) { $Dir = $answer.Trim() }
    }
}
$Dir = [IO.Path]::GetFullPath($Dir)
if ($noArguments -and $interactive -and $isAdmin) {
    $Service = [switch](Ask 'Start the server automatically with Windows (runs in the background)?' $false)
}
if ($Service -and -not $isAdmin) { Fail '-Service needs an administrator PowerShell.' }
if ($AdminPort -ne 0 -and ($AdminPort -lt 1 -or $AdminPort -gt 65535)) { Fail "'$AdminPort' is not a port number." }
if ($RemoteAdmin -and $LocalAdmin) { Fail '-RemoteAdmin and -LocalAdmin cannot be combined.' }
if ($SetupToken -and $NoSetupToken) { Fail '-SetupToken and -NoSetupToken cannot be combined.' }

if ($From) {
    if (-not (Test-Path -LiteralPath $From -PathType Container)) { Fail "'$From' is not a directory." }
    if ($Version -eq 'latest') { Fail '-From needs -Version to name the release folder.' }
    $Tag = $Version
} elseif ($Version -eq 'latest') {
    # The latest-release address redirects to the tag; resolve it so the release folder carries its name.
    $request = [Net.HttpWebRequest]::Create("https://github.com/$Repository/releases/latest")
    $request.Method = 'HEAD'
    $response = $request.GetResponse()
    $Tag = $response.ResponseUri.Segments[-1]
    $response.Close()
    if (-not $Tag -or $Tag -eq 'latest') { Fail "no published release was found for $Repository." }
    $Base = "https://github.com/$Repository/releases/latest/download"
} else {
    $Tag = $Version
    $Base = "https://github.com/$Repository/releases/download/$Tag"
}
$Asset = "bubblefighter-fantasy-win-$Arch.zip"

$Work = Join-Path ([IO.Path]::GetTempPath()) ("bff-install-" + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $Work | Out-Null
try {
    function Fetch([string]$Name) {
        $target = Join-Path $Work $Name
        if ($From) {
            $source = Join-Path $From $Name
            if (-not (Test-Path -LiteralPath $source)) { Fail "'$source' is missing." }
            Copy-Item -LiteralPath $source -Destination $target
            return
        }
        for ($attempt = 1; ; $attempt++) {
            try { Invoke-WebRequest -UseBasicParsing -Uri "$Base/$Name" -OutFile $target; return }
            catch { if ($attempt -ge 3) { Fail "downloading $Name failed: $($_.Exception.Message)" }; Start-Sleep -Seconds 2 }
        }
    }
    Step "Fetching $Asset ($Tag)"
    Fetch $Asset
    Fetch 'SHA256SUMS'
    Step 'Checking the download'
    $expected = Get-Content -LiteralPath (Join-Path $Work 'SHA256SUMS') |
        Where-Object { $_ -match "^([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($Asset))$" } | ForEach-Object { $Matches[1] } | Select-Object -First 1
    if (-not $expected) { Fail "SHA256SUMS has no line for $Asset." }
    $actual = (Get-FileHash -LiteralPath (Join-Path $Work $Asset) -Algorithm SHA256).Hash
    if ($actual -ne $expected) { Fail "the checksum of $Asset does not match SHA256SUMS." }

    # --- runtime --------------------------------------------------------------------------------------
    function Test-Runtime([string]$Dotnet) {
        if (-not $Dotnet -or -not (Test-Path -LiteralPath $Dotnet)) { return $false }
        $list = & $Dotnet --list-runtimes 2>$null
        return [bool]($list | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 10\.' })
    }
    $privateDotnet = Join-Path $Dir 'dotnet\dotnet.exe'
    $systemDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    $DotnetRoot = ''
    if (Test-Runtime $privateDotnet) {
        $DotnetRoot = Split-Path $privateDotnet
    } elseif (Test-Runtime $systemDotnet) {
        $DotnetRoot = ''
    } elseif (-not $NoRuntime) {
        Step "Installing the ASP.NET Core Runtime 10 ($Arch) into $Dir\dotnet"
        $installer = Join-Path $Work 'dotnet-install.ps1'
        Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
        & $installer -Channel 10.0 -Runtime aspnetcore -Architecture $Arch -InstallDir (Join-Path $Dir 'dotnet') -NoPath
        if (-not (Test-Runtime $privateDotnet)) { Fail 'the ASP.NET Core Runtime 10 did not install.' }
        $DotnetRoot = Split-Path $privateDotnet
    } else {
        Fail 'the ASP.NET Core Runtime 10 is missing; install it or run without -NoRuntime.'
    }
    # The executable finds a private runtime through DOTNET_ROOT; a system runtime needs nothing.
    if ($DotnetRoot) { $env:DOTNET_ROOT = $DotnetRoot }

    # --- unpack and check -------------------------------------------------------------------------------
    $releases = Join-Path $Dir 'releases'
    $release = Join-Path $releases $Tag
    $partial = "$release.partial"
    Step "Unpacking into $release"
    New-Item -ItemType Directory -Force -Path $releases, (Join-Path $Dir 'state') | Out-Null
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Recurse -Force }
    Expand-Archive -LiteralPath (Join-Path $Work $Asset) -DestinationPath $partial
    # The package holds one top-level folder; its contents become the release folder.
    $inner = Get-ChildItem -LiteralPath $partial -Directory | Select-Object -First 1
    $exe = Join-Path $inner.FullName 'BubbleFighter.Fantasy.Host.exe'
    if (-not (Test-Path -LiteralPath $exe)) { Fail "the package has no BubbleFighter.Fantasy.Host.exe." }
    & $exe profiles list --profiles-dir (Join-Path $inner.FullName 'profiles') | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'the unpacked server does not start; the previous installation is unchanged.' }

    # A running scheduled task holds the old release open; stop it before switching.
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task -and $task.State -eq 'Running') { Step 'Stopping the running server'; Stop-ScheduledTask -TaskName $TaskName }
    if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
    Move-Item -LiteralPath $inner.FullName -Destination $release
    Remove-Item -LiteralPath $partial -Recurse -Force
    $current = Join-Path $Dir 'current'
    # rmdir removes a junction without touching the release it points to.
    if (Test-Path -LiteralPath $current) { cmd /c rmdir "$current" | Out-Null }
    New-Item -ItemType Junction -Path $current -Target $release | Out-Null

    # --- WebUI access, kept in settings.env so an upgrade keeps it ----------------------------------------
    $settings = Join-Path $Dir 'settings.env'
    $saved = @{}
    if (Test-Path -LiteralPath $settings) {
        foreach ($line in Get-Content -LiteralPath $settings) { if ($line -match '^(BFF_[A-Z_]+)=(.*)$') { $saved[$Matches[1]] = $Matches[2] } }
    }
    if ($RemoteAdmin) { $remote = $true }
    elseif ($LocalAdmin) { $remote = $false }
    elseif ($saved.ContainsKey('BFF_ADMIN_HOST')) { $remote = $saved['BFF_ADMIN_HOST'] -eq '0.0.0.0' }
    else { $remote = Ask 'Allow other machines to open the WebUI (setup and administration)?' $true }
    if ($SetupToken) { $token = $true }
    elseif ($NoSetupToken) { $token = $false }
    elseif ($saved.ContainsKey('BFF_ADMIN_HOST')) { $token = [bool]$saved['BFF_SETUP_TOKEN'] }
    elseif ($remote) { $token = Ask 'Require a setup token before the first-run wizard can be used?' $false }
    else { $token = $false }
    $tokenValue = ''
    if ($token) { $tokenValue = if ($saved['BFF_SETUP_TOKEN']) { $saved['BFF_SETUP_TOKEN'] } else { New-Secret } }
    $port = if ($AdminPort) { $AdminPort } elseif ($saved['BFF_ADMIN_PORT']) { [int]$saved['BFF_ADMIN_PORT'] } else { 7300 }
    $saved['BFF_ADMIN_HOST'] = if ($remote) { '0.0.0.0' } else { '127.0.0.1' }
    $saved['BFF_SETUP_TOKEN'] = $tokenValue
    $saved['BFF_ADMIN_PORT'] = "$port"
    $lines = $saved.Keys | Sort-Object | ForEach-Object { "$_=$($saved[$_])" }
    [IO.File]::WriteAllLines($settings, [string[]]$lines)

    # --- launcher -------------------------------------------------------------------------------------
    # It always passes a command: with none the executable would open a browser and wait for a key on errors,
    # which a background task cannot answer.
    $launcher = Join-Path $Dir 'bff.cmd'
    $rootLine = if ($DotnetRoot) { "set `"DOTNET_ROOT=$DotnetRoot`"" } else { 'rem The system-wide ASP.NET Core Runtime 10 is used.' }
    $launcherText = @"
@echo off
rem Generated by install.ps1. With no arguments the server starts (the setup assistant on the first run); see "bff --help".
rem WebUI access and the setup token live in settings.env; a variable already set in the environment wins.
setlocal
for /f "usebackq eol=# tokens=1,* delims==" %%a in ("%~dp0settings.env") do if not defined %%a set "%%a=%%b"
if not defined BFF_STATE_DIR set "BFF_STATE_DIR=%~dp0state"
$rootLine
if not defined DOTNET_hostBuilder__reloadConfigOnChange set "DOTNET_hostBuilder__reloadConfigOnChange=false"
if "%~1"=="" goto start
"%~dp0current\BubbleFighter.Fantasy.Host.exe" %*
exit /b %errorlevel%
:start
"%~dp0current\BubbleFighter.Fantasy.Host.exe" start
exit /b %errorlevel%
"@
    [IO.File]::WriteAllText($launcher, ($launcherText -replace "`r?`n", "`r`n"), (New-Object Text.ASCIIEncoding))

    # --- start ------------------------------------------------------------------------------------------
    if ($noArguments -and $interactive -and -not $Service) {
        $Start = [switch](Ask 'Start the server now?' $true)
    }
    function Wait-Health {
        for ($i = 0; $i -lt 90; $i++) {
            try { if ((Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 2).status -eq 'ok') { return $true } } catch { }
            Start-Sleep -Seconds 1
        }
        return $false
    }
    $running = $false
    if ($Service) {
        Step "Registering the scheduled task $TaskName (starts with Windows, runs as SYSTEM)"
        $action = New-ScheduledTaskAction -Execute $launcher -WorkingDirectory $Dir
        $trigger = New-ScheduledTaskTrigger -AtStartup
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        $taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 `
            -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $taskSettings -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName
        Step 'Waiting for the server'
        $running = Wait-Health
        if (-not $running) { Fail "the server did not answer http://127.0.0.1:$port/health; run $launcher in a console to see why." }
    } elseif ($Start) {
        Step 'Starting the server in its own window'
        Start-Process -FilePath $launcher -WorkingDirectory $Dir
        $running = Wait-Health
        if (-not $running) { Fail "the server did not answer http://127.0.0.1:$port/health; read the error in its window." }
        Start-Process "http://127.0.0.1:$port/"
    }

    Write-Host ''
    Write-Host "Bubble Fighter Fantasy $Tag ($Arch) is installed in $Dir." -ForegroundColor Green
    if ($Service) {
        Write-Host "  Running in the background as the scheduled task $TaskName; it starts with Windows."
        Write-Host "  Stop:    Stop-ScheduledTask $TaskName      Start: Start-ScheduledTask $TaskName"
    } elseif ($running) {
        Write-Host '  Running in its own window; close that window to stop the server.'
        Write-Host "  Start:   double-click $launcher"
    } else {
        Write-Host "  Start:   double-click $launcher (close its window to stop the server)"
    }
    if ($remote) {
        Write-Host "  Open http://<this-server's-address>:$port/ to finish setup."
        Write-Host "  The WebUI accepts other machines; allow TCP port $port in the firewall or cloud security group if needed."
    } else {
        Write-Host "  Open http://127.0.0.1:$port/ to finish setup."
        Write-Host "  The WebUI accepts this machine only. From another computer, tunnel it: ssh -L ${port}:127.0.0.1:$port <user>@<server>"
        Write-Host '  (run the installer again with -RemoteAdmin to allow other machines)'
    }
    if ($tokenValue) { Write-Host "  Setup token (enter it on the first page of the wizard): $tokenValue" }
    # Without -Service this is usually a personal PC, where SQLite needs no database server.
    if (-not $Service) { Write-Host '  Database: on a personal PC, choose SQLite in the setup wizard; it needs no database server.' }
    Write-Host "  Upgrade: run this installer again; $Dir\state is kept."
} finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}
