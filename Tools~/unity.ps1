<#
.SYNOPSIS
    Send a command to the running Unity Editor via the Claude Bridge file queue.

.DESCRIPTION
    Writes a request JSON into <project>/.claude-bridge/in and waits for the
    matching response in /out. The editor drains the queue from
    EditorApplication.update, so commands execute on Unity's main thread.

    If Unity is not running, or is mid-compile, the command simply waits until
    the timeout. Nothing is lost: the request file stays queued on disk and is
    picked up whenever the editor next ticks.

.EXAMPLE
    .\tools\unity.ps1 ping
    .\tools\unity.ps1 status
    .\tools\unity.ps1 hierarchy -CmdArgs @{ depth = 3 }
    .\tools\unity.ps1 screenshot -CmdArgs @{ mode = 'game'; width = 1600; height = 900 }
    .\tools\unity.ps1 console -CmdArgs @{ type = 'Error'; count = 20 }
    .\tools\unity.ps1 sync
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Command,

    [Parameter(Position = 1)]
    [hashtable] $CmdArgs = @{},

    [int] $TimeoutSec = 30,

    [switch] $Raw
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$bridge      = Join-Path $projectRoot '.claude-bridge'
$inDir       = Join-Path $bridge 'in'
$outDir      = Join-Path $bridge 'out'

foreach ($d in @($bridge, $inDir, $outDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

function Send-BridgeCommand {
    param(
        [string]   $Cmd,
        [hashtable]$Arguments,
        [int]      $Timeout
    )

    $id      = [guid]::NewGuid().ToString('N')
    $tmpPath = Join-Path $inDir "$id.tmp"
    $reqPath = Join-Path $inDir "$id.json"
    $resPath = Join-Path $outDir "$id.json"

    $payload = @{ cmd = $Cmd; args = $Arguments } | ConvertTo-Json -Depth 12 -Compress

    # Write to .tmp then move, so the editor never reads a half-written request.
    Set-Content -Path $tmpPath -Value $payload -Encoding utf8 -NoNewline
    Move-Item -Path $tmpPath -Destination $reqPath -Force

    $deadline = (Get-Date).AddSeconds($Timeout)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $resPath) {
            $body = Get-Content -Path $resPath -Raw -Encoding utf8
            Remove-Item -Path $resPath -Force -ErrorAction SilentlyContinue
            return $body
        }
        Start-Sleep -Milliseconds 100
    }

    Remove-Item -Path $reqPath -Force -ErrorAction SilentlyContinue
    throw "Timed out after ${Timeout}s waiting for '$Cmd'. Is the Unity Editor open on this project? Check .claude-bridge/bridge-alive.json"
}

function Invoke-Bridge {
    param([string] $Cmd, [hashtable] $Arguments, [int] $Timeout)

    $body = Send-BridgeCommand -Cmd $Cmd -Arguments $Arguments -Timeout $Timeout
    if ($Raw) { return $body }

    $parsed = $body | ConvertFrom-Json
    if (-not $parsed.ok) { throw "Unity returned an error for '$Cmd':`n$($parsed.error)" }
    return $parsed.result
}

# 'sync' is a convenience macro, not a bridge command: refresh assets, wait out
# the recompile, then report anything that landed in the console meanwhile.
if ($Command -eq 'sync') {
    $refresh = Invoke-Bridge -Cmd 'refresh' -Arguments @{} -Timeout $TimeoutSec
    $since   = $refresh.logSeqBefore

    Write-Host 'Refreshing assets and waiting for compile...'

    $deadline = (Get-Date).AddSeconds(180)
    $settled  = 0
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        $st = Invoke-Bridge -Cmd 'status' -Arguments @{} -Timeout $TimeoutSec
        if (-not $st.isCompiling -and -not $st.isUpdating) {
            $settled++
            # Require two consecutive clean polls: Unity briefly reports idle
            # between the asset import pass and the script compile pass.
            if ($settled -ge 2) { break }
        } else {
            $settled = 0
        }
    }

    $log = Invoke-Bridge -Cmd 'console' -Arguments @{ since = $since; count = 100 } -Timeout $TimeoutSec
    $bad = @($log.entries | Where-Object { $_.type -eq 'Error' -or $_.type -eq 'Exception' })

    if ($bad.Count -eq 0) {
        Write-Host 'Compile clean.' -ForegroundColor Green
    } else {
        Write-Host "$($bad.Count) error(s):" -ForegroundColor Red
        $bad | ForEach-Object { Write-Host "  [$($_.type)] $($_.message)" -ForegroundColor Red }
    }
    return
}

# 'test' is a convenience macro: start a run, poll until it finishes, summarize.
# EditMode runs finish in seconds; PlayMode runs reload the domain part way
# through, which is why results are polled from a file rather than awaited.
if ($Command -eq 'test') {
    $start = Invoke-Bridge -Cmd 'tests' -Arguments $CmdArgs -Timeout $TimeoutSec
    Write-Host "Running $($start.mode) tests (run $($start.runId))..."

    $deadline = (Get-Date).AddSeconds(600)
    $run = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 700
        try {
            $run = Invoke-Bridge -Cmd 'testresults' -Arguments @{ runId = $start.runId } -Timeout $TimeoutSec
        } catch {
            # A PlayMode run reloads the domain; the bridge is briefly unavailable.
            continue
        }
        if ($run.finished) { break }
    }

    if ($null -eq $run -or -not $run.finished) {
        throw "Test run $($start.runId) did not finish in time. Results file: $($start.resultsFile)"
    }

    $failed = @($run.tests | Where-Object { $_.status -eq 'Failed' })

    Write-Host ''
    foreach ($t in $failed) {
        Write-Host "FAILED  $($t.name)" -ForegroundColor Red
        if ($t.message) { Write-Host "        $($t.message)" -ForegroundColor Red }
    }

    $summary = "$($run.passed) passed, $($run.failed) failed, $($run.skipped) skipped in $($run.durationSec)s"
    if ($run.failed -gt 0) {
        Write-Host $summary -ForegroundColor Red
    } else {
        Write-Host $summary -ForegroundColor Green
    }
    return
}

$result = Invoke-Bridge -Cmd $Command -Arguments $CmdArgs -Timeout $TimeoutSec
$result | ConvertTo-Json -Depth 12
