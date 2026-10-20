# Publish Linux binaries without a bundled runtime; the Docker image provides ASP.NET Core.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [ValidateSet('linux-x64', 'linux-arm64')][string]$Runtime = 'linux-x64'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskOutputRoot = [IO.Path]::GetFullPath($OutputPath)
if ((Test-Path -LiteralPath $taskOutputRoot) -and @(Get-ChildItem -LiteralPath $taskOutputRoot -Force).Count -gt 0) {
    throw 'Publish into an empty directory to avoid stale binaries or bundled runtime files.'
}
Push-Location -LiteralPath $taskRepoRoot
try {
    & dotnet publish src/BubbleFighter.Fantasy.Host -c Release -r $Runtime --self-contained false `
        -p:PublishTrimmed=false -p:PublishAot=false -o $taskOutputRoot
    if ($LASTEXITCODE -ne 0) { throw 'Linux publish failed.' }
    foreach ($taskRuntimeFile in @('libcoreclr.so', 'libhostfxr.so', 'libhostpolicy.so', 'System.Private.CoreLib.dll')) {
        if (Test-Path -LiteralPath (Join-Path $taskOutputRoot $taskRuntimeFile)) {
            throw "The publish unexpectedly contains a bundled runtime: $taskRuntimeFile"
        }
    }
    $taskExecutable = [IO.File]::ReadAllBytes((Join-Path $taskOutputRoot 'BubbleFighter.Fantasy.Host'))
    if ($taskExecutable.Length -lt 20 -or [BitConverter]::ToUInt32($taskExecutable, 0) -ne 0x464c457f) {
        throw 'The published apphost is not an ELF executable.'
    }
    $taskMachine = if ($Runtime -eq 'linux-x64') { 62 } else { 183 }
    if ([BitConverter]::ToUInt16($taskExecutable, 18) -ne $taskMachine) {
        throw 'The published apphost has an unexpected Linux architecture.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $taskOutputRoot 'plugins/BubbleFighter.Fantasy.InGame/BubbleFighter.Fantasy.InGame.dll'))) {
        throw 'The publish is missing dynamically loaded plugins.'
    }
    Write-Host "Linux framework-dependent publish ready: $taskOutputRoot ($Runtime)"
}
finally { Pop-Location }
