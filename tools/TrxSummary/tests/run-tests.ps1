# Requires PowerShell 7 and an already-built tool; this script never builds or installs packages.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ToolDll,
    [string]$ResultsDirectory = [System.IO.Path]::GetTempPath()
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$toolPath = (Resolve-Path -LiteralPath $ToolDll).Path
$fixtures = Join-Path $PSScriptRoot 'fixtures'
$runPath = Join-Path ([System.IO.Path]::GetFullPath($ResultsDirectory)) ('trx-summary-' + [guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($runPath)
$cases = [System.Collections.Generic.List[object]]::new()
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Write-InputList([string]$Name, [string[]]$Paths) {
    $path = Join-Path $runPath ($Name + '.txt')
    [System.IO.File]::WriteAllLines($path, $Paths, $utf8)
    return $path
}

function Invoke-Case([string]$Name, [string[]]$ToolArguments, [int]$ExpectedExit, [string[]]$ExpectedText = @()) {
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($toolPath)
    foreach ($argument in $ToolArguments) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Cannot start case $Name." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            $process.Kill($true)
            $process.WaitForExit()
            [System.IO.File]::WriteAllText((Join-Path $runPath ($Name + '.stdout.txt')), $stdoutTask.GetAwaiter().GetResult(), $utf8)
            [System.IO.File]::WriteAllText((Join-Path $runPath ($Name + '.stderr.txt')), $stderrTask.GetAwaiter().GetResult(), $utf8)
            throw "Case $Name exceeded its 15-second bound; only its own process tree was stopped."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [System.IO.File]::WriteAllText((Join-Path $runPath ($Name + '.stdout.txt')), $stdout, $utf8)
        [System.IO.File]::WriteAllText((Join-Path $runPath ($Name + '.stderr.txt')), $stderr, $utf8)
        $cases.Add([pscustomobject]@{ name = $Name; actualExit = $process.ExitCode; expectedExit = $ExpectedExit })
        if ($process.ExitCode -ne $ExpectedExit) { throw "$Name returned $($process.ExitCode), expected $ExpectedExit. See $runPath." }
        if ($ExpectedExit -ne 0 -and $stdout.Length -ne 0) { throw "$Name wrote a partial report on failure." }
        foreach ($text in $ExpectedText) {
            if (-not ($stdout + $stderr).Contains($text, [System.StringComparison]::Ordinal)) { throw "$Name did not report '$text'." }
        }
        return $stdout
    }
    finally { $process.Dispose() }
}

try {
    $passed = Join-Path $fixtures 'passed.trx'
    $single = Write-InputList 'single' @($passed)
    $duplicate = Write-InputList 'duplicate' @($passed, $passed)
    $hash = (Get-FileHash -LiteralPath $passed -Algorithm SHA256).Hash
    $null = Invoke-Case 'passed-and-same-byte-hash' @($single) 0 @($hash, 'run outcome   Completed', 'report-only', 'not a test-pass')
    $report = Invoke-Case 'duplicate-record-counting' @($duplicate, '--max-files', '2') 0 @('DUPLICATE INPUT', 'appears 2 times')
    if ($report -notmatch '(?m)^total\s+2\s*$') { throw 'Duplicate input must retain record-counting semantics.' }

    $aborted = Write-InputList 'aborted' @((Join-Path $fixtures 'aborted.trx'))
    $null = Invoke-Case 'aborted-is-report-only-success' @($aborted) 0 @('run outcome   Aborted', 'RunInfo outcome=Error', 'adapter terminated', 'Run ErrorInfo:', 'host disconnected')
    $mismatch = Write-InputList 'mismatch' @((Join-Path $fixtures 'mismatched-counters.trx'))
    $null = Invoke-Case 'counter-mismatch-is-visible' @($mismatch) 0 @('COUNTER MISMATCH', 'reported 2, parsed records 1')
    $missing = Write-InputList 'missing-summary' @((Join-Path $fixtures 'missing-summary.trx'))
    $null = Invoke-Case 'missing-summary-is-visible' @($missing) 0 @('Missing ResultSummary', 'run outcome   (missing)')
    foreach ($fixture in @('aggregate', 'nested')) {
        $list = Write-InputList $fixture @((Join-Path $fixtures ($fixture + '.trx')))
        $null = Invoke-Case ($fixture + '-rejected') @($list) 4 @('unsupported aggregate or nested result structure')
    }
    $dtd = Write-InputList 'dtd' @((Join-Path $fixtures 'dtd.trx'))
    $null = Invoke-Case 'dtd-rejected' @($dtd) 4 @('malformed XML')

    $null = Invoke-Case 'zero-option-rejected' @($single, '--max-results', '0') 2 @('positive 32-bit integer')
    $null = Invoke-Case 'negative-option-rejected' @($single, '--max-files', '-1') 2 @('positive 32-bit integer')
    $null = Invoke-Case 'unknown-option-rejected' @($single, '--unknown', '1') 2 @('Unknown option')
    $null = Invoke-Case 'repeated-option-rejected' @($single, '--max-files', '2', '--max-files', '3') 2 @('was repeated')
    $null = Invoke-Case 'file-count-cap' @($duplicate, '--max-files', '1') 3 @('--max-files')
    $null = Invoke-Case 'file-byte-cap' @($single, '--max-file-bytes', '1') 3 @('--max-file-bytes')
    $fileBytes = (Get-Item -LiteralPath $passed).Length
    $null = Invoke-Case 'aggregate-byte-cap' @($duplicate, '--max-total-bytes', $fileBytes.ToString()) 3 @('--max-total-bytes')
    $null = Invoke-Case 'aggregate-result-cap' @($duplicate, '--max-results', '1') 4 @('--max-results')

    $commentList = Join-Path $runPath 'long-comment.txt'
    [System.IO.File]::WriteAllText($commentList, '#' + ('x' * 1024), $utf8)
    $null = Invoke-Case 'long-comment-byte-cap' @($commentList, '--max-list-bytes', '128') 3 @('--max-list-bytes')
    $longLineList = Join-Path $runPath 'long-line.txt'
    [System.IO.File]::WriteAllText($longLineList, ('x' * 1024), $utf8)
    $null = Invoke-Case 'long-line-byte-cap' @($longLineList, '--max-list-bytes', '128') 3 @('--max-list-bytes')

    # The compact fixture fits below this per-document XML limit; repeated parsed text does not.
    $xmlLength = [System.IO.File]::ReadAllText($passed).Length
    $textLimit = ($xmlLength + 1).ToString()
    $null = Invoke-Case 'single-text-budget' @($single, '--max-text-chars', $textLimit) 0 @('report-only')
    $null = Invoke-Case 'aggregate-text-budget' @($duplicate, '--max-text-chars', $textLimit) 4 @('--max-text-chars')

    $invalidCounterPath = Join-Path $runPath 'invalid-counter.trx'
    [System.IO.File]::WriteAllText($invalidCounterPath, [System.IO.File]::ReadAllText($passed).Replace('total="1"', 'total="-1"'), $utf8)
    $invalidCounterList = Write-InputList 'invalid-counter' @($invalidCounterPath)
    $null = Invoke-Case 'negative-counter-rejected' @($invalidCounterList) 4 @('must be a nonnegative 32-bit integer')

    $duplicateSummaryPath = Join-Path $runPath 'duplicate-summary.trx'
    [System.IO.File]::WriteAllText($duplicateSummaryPath, [System.IO.File]::ReadAllText($passed).Replace('</TestRun>', '<ResultSummary outcome="Completed" /></TestRun>'), $utf8)
    $duplicateSummaryList = Write-InputList 'duplicate-summary' @($duplicateSummaryPath)
    $null = Invoke-Case 'duplicate-summary-rejected' @($duplicateSummaryList) 4 @('multiple <ResultSummary>')

    Write-Output ("Passed {0} integration cases. Evidence: {1}" -f $cases.Count, $runPath)
}
finally {
    [System.IO.File]::WriteAllText((Join-Path $runPath 'cases.json'), (ConvertTo-Json -InputObject $cases.ToArray() -Depth 4), $utf8)
    Write-Output ("Evidence retained at {0}" -f $runPath)
}
