<#
.SYNOPSIS
    Stops the applications run-local.ps1 started.

.DESCRIPTION
    Stops the two APIs, the dashboard dev server and the Windows agent.

    PostgreSQL and Redis are ordinary local services with their own lifecycle and
    are deliberately left alone: this script did not start them, and stopping
    them would take out anything else on the machine that uses them. Stop them
    the usual way if you want to (Services, or `net stop`).

    Nothing here ever drops a database. The PostgreSQL database holds the audit
    trail, the enrolled devices and your admin account.

.EXAMPLE
    .\scripts\stop-local.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# --- applications ----------------------------------------------------------
foreach ($name in 'EndpointPlatform.Api', 'EndpointPlatform.AgentApi', 'EndpointAgent.Service') {
    $processes = Get-Process -Name $name -ErrorAction SilentlyContinue
    if (-not $processes) {
        Write-Host ("  {0,-26} not running" -f $name) -ForegroundColor DarkGray
        continue
    }

    foreach ($process in $processes) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            Write-Host ("  {0,-26} stopped (PID {1})" -f $name, $process.Id) -ForegroundColor Green
        }
        catch {
            # The agent runs elevated, so an ordinary window cannot signal it.
            # That is the isolation working, not a bug - say so plainly.
            Write-Host ("  {0,-26} ACCESS DENIED (PID {1})" -f $name, $process.Id) -ForegroundColor Yellow
            Write-Host '      It runs elevated. Close its window, or re-run this script as administrator.' -ForegroundColor DarkGray
        }
    }
}

# --- dashboard -------------------------------------------------------------
# Matched by listening port rather than by process name: killing every `node`
# would take out unrelated work.
$listener = Get-NetTCPConnection -LocalPort 5173 -State Listen -ErrorAction SilentlyContinue |
    Select-Object -First 1

if ($listener) {
    try {
        Stop-Process -Id $listener.OwningProcess -Force -ErrorAction Stop
        Write-Host ("  {0,-26} stopped (PID {1})" -f 'Dashboard :5173', $listener.OwningProcess) -ForegroundColor Green
    }
    catch {
        Write-Host ("  {0,-26} could not stop PID {1}" -f 'Dashboard :5173', $listener.OwningProcess) -ForegroundColor Yellow
    }
}
else {
    Write-Host ("  {0,-26} not running" -f 'Dashboard :5173') -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'PostgreSQL and Redis are untouched (this script never started them).' -ForegroundColor DarkGray
Write-Host ''
Write-Host 'Start again with: .\scripts\run-local.ps1 -WithAgent' -ForegroundColor DarkGray
