param([string]$SettingsPath = (Join-Path $PSScriptRoot 'server-settings.local.json'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
$taskConsoleEcho = $false
if ($settings.PSObject.Properties['console_echo']) {
    if ($settings.console_echo -isnot [bool]) { throw 'console_echo must be a Boolean.' }
    $taskConsoleEcho = $settings.console_echo
}
$profile = if ($settings.PSObject.Properties['profile']) { [string]$settings.profile } else { 'all' }
if ($profile -notmatch '^[A-Za-z0-9_-]+$') { throw 'Invalid profile name.' }
$mapping = @{
    bind_host='BFF_BIND_HOST'; public_host='BFF_PUBLIC_HOST'; game_port='CN_324_GAME_PORT'
    public_game_port='CN_324_PUBLIC_PORT'; admin_host='CN_324_ADMIN_HOST'; admin_port='CN_324_ADMIN_PORT'
    mysql_host='BFF_MYSQL_HOST'; mysql_port='BFF_MYSQL_PORT'; mysql_user='BFF_MYSQL_USER'
    mysql_password='BFF_MYSQL_PASSWORD'; mysql_database='BFF_MYSQL_DATABASE'
}
if ($settings.PSObject.Properties['environment']) {
    foreach ($property in $settings.environment.PSObject.Properties) {
        if ($property.Name -notmatch '^[A-Z][A-Z0-9_]*$') { throw 'Invalid environment variable name.' }
        [Environment]::SetEnvironmentVariable($property.Name, [string]$property.Value, 'Process')
    }
}
[Environment]::SetEnvironmentVariable('BFF_ADMIN_HOST', [string]$settings.admin_host, 'Process')
[Environment]::SetEnvironmentVariable('BFF_ADMIN_PORT', [string]$settings.admin_port, 'Process')
foreach ($key in $mapping.Keys) {
    $property = $settings.PSObject.Properties[$key]
    if ($null -eq $property) { throw "Missing setting: $key" }
    [Environment]::SetEnvironmentVariable($mapping[$key], [string]$property.Value, 'Process')
}
Push-Location -LiteralPath (Join-Path $PSScriptRoot 'server')
try {
    $taskLogStamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $taskLogDate = ''
    $taskLogWriter = $null
    $taskLogRoot = Join-Path (Get-Location) 'logs'
    try {
        & '.\BubbleFighter.Fantasy.Host.exe' run --profile $profile 2>&1 | ForEach-Object {
            $taskCurrentDate = Get-Date -Format 'yyyy-MM-dd'
            if ($taskCurrentDate -ne $taskLogDate) {
                if ($taskLogWriter) { $taskLogWriter.Dispose() }
                $taskLogDate = $taskCurrentDate
                $taskDayDirectory = Join-Path $taskLogRoot $taskLogDate
                [IO.Directory]::CreateDirectory($taskDayDirectory) | Out-Null
                $taskLogPath = Join-Path $taskDayDirectory "server_${taskLogStamp}.log"
                $taskLogWriter = [IO.StreamWriter]::new($taskLogPath, $true, [Text.UTF8Encoding]::new($false))
                $taskLogWriter.AutoFlush = $true
                Write-Host "Full server log: $taskLogPath"
            }
            if ($taskLogWriter.BaseStream.Length -ge 10485760) {
                $taskLogWriter.Dispose()
                Move-Item -LiteralPath $taskLogPath -Destination ($taskLogPath + '.1') -Force
                $taskLogWriter = [IO.StreamWriter]::new($taskLogPath, $false, [Text.UTF8Encoding]::new($false))
                $taskLogWriter.AutoFlush = $true
            }
            $taskLogWriter.WriteLine([string]$_)
            $taskLine = [string]$_
            if ($taskConsoleEcho -or $taskLine -match 'Server started with|Shutdown complete|\b(warn|fail|crit|error):|Packet log writer failed|packet-log-overflow') {
                Write-Host $taskLine
            }
        }
    }
    finally { if ($taskLogWriter) { $taskLogWriter.Dispose() } }
    if ($LASTEXITCODE -ne 0) { throw "Server exited with code $LASTEXITCODE. See console output." }
}
finally { Pop-Location }
