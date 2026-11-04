#!/usr/bin/env pwsh
<#
.SYNOPSIS
Smoke-tests an installed ai-proxy tool shim; exits non-zero on the first failed check.

.PARAMETER Tool
Path to the installed shim (ai-proxy or ai-proxy.exe).

.PARAMETER ExpectedVersion
The version `--version` must print.

.PARAMETER SkipImmediateEof
Skips check 6 (mcp reply when stdin closes right after the requests).
#>
param(
    [Parameter(Mandatory)][string]$Tool,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [switch]$SkipImmediateEof
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$deadlineSeconds = 20
$deadChannel = 'http://127.0.0.1:1'

function Fail([string]$check, [string]$message) {
    [Console]::Error.WriteLine("smoke FAILED at check ${check}: $message")
    exit 1
}

function Start-Tool([string[]]$ToolArguments) {
    $info = [System.Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $Tool).Path)
    foreach ($argument in $ToolArguments) { $info.ArgumentList.Add($argument) }
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    [void]$process.Start()
    $process
}

# Spectre colours its output when it detects CI, even into a pipe; checks match plain text.
function Remove-Ansi([string]$text) {
    if ($null -eq $text) { return '' }
    $text -replace "`e\[[0-9;?]*[A-Za-z]", ''
}

function Stop-Tool($process) {
    if (-not $process.HasExited) { $process.Kill($true) }
}

# Runs a command to completion with stdin closed; returns exit code and both streams.
function Invoke-Tool([string[]]$ToolArguments) {
    $process = Start-Tool $ToolArguments
    try {
        $process.StandardInput.Close()
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($deadlineSeconds * 1000)) {
            Stop-Tool $process
            return [pscustomobject]@{ TimedOut = $true; ExitCode = -1; Out = ''; Err = '' }
        }
        $process.WaitForExit()
        [pscustomobject]@{ TimedOut = $false; ExitCode = $process.ExitCode; Out = (Remove-Ansi $out.Result); Err = (Remove-Ansi $err.Result) }
    }
    finally { Stop-Tool $process; $process.Dispose() }
}

function Assert-Completed([string]$check, $result) {
    if ($result.TimedOut) { Fail $check "the command did not exit within $deadlineSeconds seconds." }
}

$mcpRequests = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}',
    '{"jsonrpc":"2.0","method":"notifications/initialized"}',
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
)

function Get-ResponseId([string]$line) {
    try { $json = $line | ConvertFrom-Json -ErrorAction Stop } catch { return $null }
    if ($json -is [pscustomobject] -and $json.PSObject.Properties['id']) { return $json.id }
    $null
}

# 1. --version
$result = Invoke-Tool @('--version')
Assert-Completed '1 (--version)' $result
if ($result.Out.Trim() -ne $ExpectedVersion) {
    Fail '1 (--version)' "expected '$ExpectedVersion', got '$($result.Out.Trim())' (exit $($result.ExitCode))."
}
Write-Host "ok 1: --version is $ExpectedVersion"

# 2. --help
$result = Invoke-Tool @('--help')
Assert-Completed '2 (--help)' $result
if ($result.ExitCode -ne 0) { Fail '2 (--help)' "exit code $($result.ExitCode), expected 0." }
$helpText = $result.Out + $result.Err
foreach ($command in 'gain', 'mcp', 'harness', 'doctor') {
    if ($helpText -notmatch "\b$command\b") { Fail '2 (--help)' "output does not mention '$command'." }
}
Write-Host 'ok 2: --help lists gain, mcp, harness, doctor'

# 3. gain against a dead endpoint
$result = Invoke-Tool @('gain', '--format', 'json', '--endpoint', $deadChannel)
Assert-Completed '3 (gain)' $result
if ($result.ExitCode -ne 1) { Fail '3 (gain)' "exit code $($result.ExitCode), expected 1." }
if (-not $result.Err.StartsWith('ai-proxy:')) { Fail '3 (gain)' "stderr does not start with 'ai-proxy:': '$($result.Err.Trim())'." }
Write-Host 'ok 3: gain fails cleanly when the proxy is unreachable'

# 4. doctor with nothing running
$emptyDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ai-proxy-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $emptyDirectory | Out-Null
try {
    $result = Invoke-Tool @('doctor', '--format', 'json', '--endpoint', $deadChannel, '--proxy', $deadChannel, '--data-directory', $emptyDirectory)
}
finally { Remove-Item -LiteralPath $emptyDirectory -Recurse -Force -ErrorAction SilentlyContinue }
Assert-Completed '4 (doctor)' $result
if ($result.ExitCode -ne 1) { Fail '4 (doctor)' "exit code $($result.ExitCode), expected 1. stderr: $($result.Err.Trim())" }
try { $report = $result.Out | ConvertFrom-Json -ErrorAction Stop }
catch { Fail '4 (doctor)' "stdout is not JSON: '$($result.Out.Trim())'." }
if (-not $report.PSObject.Properties['checks'] -or $null -eq $report.checks) { Fail '4 (doctor)' "JSON has no 'checks' array." }
Write-Host "ok 4: doctor reports $(@($report.checks).Count) checks and exits 1"

# 5. mcp with stdin held open
$process = Start-Tool @('mcp', '--endpoint', $deadChannel)
$null = $process.StandardError.ReadToEndAsync()
try {
    foreach ($line in $mcpRequests) { $process.StandardInput.WriteLine($line) }
    $process.StandardInput.Flush()
    $found = $null
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($null -eq $found) {
        $remaining = [int]($deadlineSeconds * 1000 - $stopwatch.ElapsedMilliseconds)
        if ($remaining -le 0) { Fail '5 (mcp, stdin open)' "no reply with id 2 within $deadlineSeconds seconds." }
        $read = $process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait($remaining)) { Fail '5 (mcp, stdin open)' "no reply with id 2 within $deadlineSeconds seconds." }
        if ($null -eq $read.Result) { Fail '5 (mcp, stdin open)' 'stdout ended before the id 2 reply arrived.' }
        if ((Get-ResponseId $read.Result) -eq 2) { $found = $read.Result }
    }
    if ($found -notmatch 'retrieve_original') { Fail '5 (mcp, stdin open)' "tools/list reply lacks retrieve_original: $found" }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit($deadlineSeconds * 1000)) { Fail '5 (mcp, stdin open)' "process did not exit within $deadlineSeconds seconds after stdin closed." }
    if ($process.ExitCode -ne 0) { Fail '5 (mcp, stdin open)' "exit code $($process.ExitCode), expected 0." }
}
finally { Stop-Tool $process; $process.Dispose() }
Write-Host 'ok 5: mcp answers tools/list with retrieve_original and exits 0 on stdin close'

# 6. mcp with stdin closed straight after the requests
if ($SkipImmediateEof) {
    Write-Host 'skip 6: immediate-EOF check (-SkipImmediateEof)'
}
else {
    $process = Start-Tool @('mcp', '--endpoint', $deadChannel)
    try {
        foreach ($line in $mcpRequests) { $process.StandardInput.WriteLine($line) }
        $process.StandardInput.Close()
        $all = $process.StandardOutput.ReadToEndAsync()
        $null = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($deadlineSeconds * 1000)) { Fail '6 (mcp, immediate EOF)' "process did not exit within $deadlineSeconds seconds." }
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { Fail '6 (mcp, immediate EOF)' "exit code $($process.ExitCode), expected 0." }
        $ids = @($all.Result -split "`r?`n" | Where-Object { $_ } | ForEach-Object { Get-ResponseId $_ })
        foreach ($expected in 1, 2) {
            if ($ids -notcontains $expected) { Fail '6 (mcp, immediate EOF)' "no reply with id $expected before the process exited; got ids: [$($ids -join ', ')]." }
        }
    }
    finally { Stop-Tool $process; $process.Dispose() }
    Write-Host 'ok 6: mcp answers both requests when stdin closes immediately'
}

Write-Host 'smoke passed'
exit 0
