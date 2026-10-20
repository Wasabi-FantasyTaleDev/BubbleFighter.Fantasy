# Publish the host and all plugins without bundling the shared runtime.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskOutputRoot = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $taskOutputRoot) {
    if (@(Get-ChildItem -LiteralPath $taskOutputRoot -Force).Count -gt 0) {
        throw 'Publish into an empty directory to avoid retaining runtime files from an older release.'
    }
}
Push-Location -LiteralPath $taskRepoRoot
try {
    & dotnet publish src/BubbleFighter.Fantasy.Host -c Release -r win-x64 --self-contained false `
        -p:PublishTrimmed=false -p:PublishAot=false -o $taskOutputRoot
    if ($LASTEXITCODE -ne 0) { throw 'Windows x64 publish failed.' }
    $taskRuntimeFiles = @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')
    foreach ($taskRuntimeFile in $taskRuntimeFiles) {
        if (Test-Path -LiteralPath (Join-Path $taskOutputRoot $taskRuntimeFile)) {
            throw "The publish unexpectedly contains a bundled runtime: $taskRuntimeFile"
        }
    }
    $taskExecutable = [IO.File]::ReadAllBytes((Join-Path $taskOutputRoot 'BubbleFighter.Fantasy.Host.exe'))
    $taskPeOffset = [BitConverter]::ToInt32($taskExecutable, 0x3c)
    if ([BitConverter]::ToUInt16($taskExecutable, $taskPeOffset + 4) -ne 0x8664) {
        throw 'The published executable is not Windows x64.'
    }
    Write-Host "Windows x64 framework-dependent publish ready: $taskOutputRoot"
    Write-Host 'The deployment server requires the x64 ASP.NET Core Runtime 10.0, which includes the .NET runtime.'
}
finally { Pop-Location }
