param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [ValidateRange(1, 50)][int]$Iterations = 5
)

$ErrorActionPreference = 'Stop'
$gatewayExecutable = (Resolve-Path -LiteralPath $Executable).Path
$gatewayResults = @()
for ($gatewayIteration = 0; $gatewayIteration -lt $Iterations; $gatewayIteration++) {
    $gatewayPipeName = 'fantasy-tale-gateway-' + [Guid]::NewGuid().ToString('N')
    $gatewayPipe = [IO.Pipes.NamedPipeServerStream]::new($gatewayPipeName, [IO.Pipes.PipeDirection]::InOut, 1,
        [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly)
    $gatewayTimeout = [Threading.CancellationTokenSource]::new(10000)
    $gatewayProcess = [Diagnostics.Process]::new()
    $gatewayProcess.StartInfo = [Diagnostics.ProcessStartInfo]::new($gatewayExecutable)
    $gatewayProcess.StartInfo.UseShellExecute = $false
    $gatewayProcess.StartInfo.CreateNoWindow = $true
    $gatewayProcess.StartInfo.RedirectStandardError = $true
    $gatewayProcess.StartInfo.ArgumentList.Add('--pipe')
    $gatewayProcess.StartInfo.ArgumentList.Add($gatewayPipeName)
    $gatewayWatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (!$gatewayProcess.Start()) { throw 'Failed to start Fantasy Tale Gateway.' }
        $gatewayErrorTask = $gatewayProcess.StandardError.ReadToEndAsync()
        $gatewayPipe.WaitForConnectionAsync($gatewayTimeout.Token).GetAwaiter().GetResult()
        $gatewayConfiguration = @{
            schema_version = 1
            rows = @(@{
                id = 'selector'; plugin = 'bf-center-selector'
                config = @{
                    build = 'kr-812'
                    centers = @(
                        @{ id = 'one'; name = 'One'; host = '192.0.2.10'; port = 7110 },
                        @{ id = 'two'; name = 'Two'; host = '198.51.100.20'; port = 7210 }
                    )
                }
            })
        } | ConvertTo-Json -Depth 8 -Compress
        $gatewayBytes = [Text.Encoding]::UTF8.GetBytes($gatewayConfiguration)
        $gatewayHeader = [BitConverter]::GetBytes([int]$gatewayBytes.Length)
        $gatewayPipe.WriteAsync($gatewayHeader, 0, 4, $gatewayTimeout.Token).GetAwaiter().GetResult()
        $gatewayPipe.WriteAsync($gatewayBytes, 0, $gatewayBytes.Length, $gatewayTimeout.Token).GetAwaiter().GetResult()
        $null = $gatewayPipe.ReadExactlyAsync([Memory[byte]]::new($gatewayHeader), $gatewayTimeout.Token).AsTask().GetAwaiter().GetResult()
        $gatewayLength = [BitConverter]::ToInt32($gatewayHeader)
        if ($gatewayLength -lt 1 -or $gatewayLength -gt 65536) { throw 'Invalid READY length.' }
        $gatewayReply = [byte[]]::new($gatewayLength)
        $null = $gatewayPipe.ReadExactlyAsync([Memory[byte]]::new($gatewayReply), $gatewayTimeout.Token).AsTask().GetAwaiter().GetResult()
        $gatewayReadyMs = $gatewayWatch.Elapsed.TotalMilliseconds
        $gatewayReady = [Text.Encoding]::UTF8.GetString($gatewayReply) | ConvertFrom-Json
        if ($gatewayReady.type -ne 'ready' -or $gatewayReady.rows[0].mode -ne 'bootstrap') { throw 'Invalid READY.' }
        $gatewayProcess.Refresh()
        $gatewaySample = [ordered]@{
            iteration = $gatewayIteration + 1
            ready_ms = [Math]::Round($gatewayReadyMs, 2)
            working_set_bytes = $gatewayProcess.WorkingSet64
            private_bytes = $gatewayProcess.PrivateMemorySize64
            cpu_ms = [Math]::Round($gatewayProcess.TotalProcessorTime.TotalMilliseconds, 2)
            window_handle = $gatewayProcess.MainWindowHandle.ToInt64()
        }
        if ($gatewaySample.window_handle -ne 0) { throw 'Unexpected visible Gateway window.' }
        if ($gatewayIteration % 2 -eq 0) {
            $gatewayBytes = [Text.Encoding]::UTF8.GetBytes('{"type":"stop"}')
            $gatewayHeader = [BitConverter]::GetBytes([int]$gatewayBytes.Length)
            $gatewayPipe.Write($gatewayHeader)
            $gatewayPipe.Write($gatewayBytes)
            $gatewayPipe.Flush()
        } else {
            $gatewayPipe.Dispose()
        }
        $null = $gatewayProcess.WaitForExitAsync($gatewayTimeout.Token).GetAwaiter().GetResult()
        if ($gatewayProcess.ExitCode -ne 0) { throw $gatewayErrorTask.GetAwaiter().GetResult() }
        $gatewaySample.exit_code = $gatewayProcess.ExitCode
        $gatewayResults += [pscustomobject]$gatewaySample
    } catch {
        if ($gatewayProcess.HasExited) { Write-Output $gatewayErrorTask.GetAwaiter().GetResult() }
        throw
    } finally {
        $gatewayPipe.Dispose()
        if ($gatewayProcess.Id -and !$gatewayProcess.HasExited) { $gatewayProcess.Kill($true); $gatewayProcess.WaitForExit() }
        $gatewayProcess.Dispose()
        $gatewayTimeout.Dispose()
    }
}
$gatewayResults | ConvertTo-Json
