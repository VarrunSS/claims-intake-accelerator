<#
.SYNOPSIS
    Starts the whole system locally: the legacy mock, the review API, the inbox watcher and the review UI, with the dev sign-in.

.DESCRIPTION
    Local mode (docs/adr/0005-local-development-mode.md). Needs the .NET 10 SDK, and Node.js for the UI.
    No Azure account, keys or Docker. Press Ctrl+C to stop everything.

      Legacy mock    http://localhost:5200
      Review API     http://localhost:5100
      Review UI      http://localhost:4200   (skipped with -NoUi)
      Inbox          ./local-inbox           (drop a file from samples/ here)

    Output of each process goes to .local-run/<name>.log (and <name>.err.log).

    The services run as their own apphost processes (not through `dotnet run`), so the process the script starts is the one that
    listens, and stopping it stops the service. Cleanup does not rely on the script finishing normally: it kills the process
    trees it started, then any ClaimsIntake apphost still running from this repository (left by an earlier run that was killed), and the
    process ids are in .local-run/pids.txt for `-Stop`.

.PARAMETER NoUi
    Do not start the Angular dev server.

.PARAMETER ExitAfterSeconds
    Stop after this many seconds instead of waiting for Ctrl+C. Useful for smoke tests.

.PARAMETER Exporter
    Telemetry:Exporter for the services: none (default), console (a span per stage in .local-run/*.log) or otlp.

.PARAMETER Stop
    Do not start anything: stop what an earlier run left behind (the processes in .local-run/pids.txt and any apphost from this repository).

.EXAMPLE
    ./scripts/run-local.ps1
    Copy-Item samples/01-water-damage-clean.pdf local-inbox/

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-local.ps1 -NoUi -Exporter console -ExitAfterSeconds 60
#>
[CmdletBinding()]
param(
    [switch]$NoUi,
    [int]$ExitAfterSeconds = 0,
    [ValidateSet('none', 'console', 'otlp')]
    [string]$Exporter = 'none',
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$logs = Join-Path $root '.local-run'
$inbox = Join-Path $root 'local-inbox'
$pidFile = Join-Path $logs 'pids.txt'
New-Item -ItemType Directory -Force -Path $logs, $inbox | Out-Null

$script:started = @()

# Kills a process and everything it started. A process that has already gone is not an error.
function Stop-Tree([int]$ProcessId) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($IsLinux -or $IsMacOS) {
            & pkill -TERM -P $ProcessId 2>$null
            Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
        } else {
            $null = & taskkill.exe /PID $ProcessId /T /F 2>&1
        }
    } finally {
        $ErrorActionPreference = $previous
    }
}

# ClaimsIntake apphosts that are running from this repository, whoever started them.
function Get-RepositoryApphosts {
    Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessName -like 'ClaimsIntake.*' -and $_.Path -and $_.Path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)
    }
}

function Stop-Everything {
    foreach ($p in $script:started) {
        if (-not $p.HasExited) { Stop-Tree $p.Id }
    }
    if (Test-Path $pidFile) {
        foreach ($line in Get-Content $pidFile) {
            if ($line -match '^\d+$') { Stop-Tree ([int]$line) }
        }
        Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
    }
    # An apphost that outlived its parent (an earlier run that was killed) would also lock the build output.
    foreach ($p in @(Get-RepositoryApphosts)) {
        Stop-Tree $p.Id
    }
}

if ($Stop) {
    Stop-Everything
    Write-Host 'Stopped.'
    return
}

# Stop what an earlier run left behind: it would hold the ports and lock the files the build is about to replace.
Stop-Everything

Write-Host 'Building...'
dotnet build (Join-Path $root 'ClaimsIntake.sln') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }

function Start-Service([string]$Name, [string]$Project, [string[]]$Arguments) {
    $projectDir = Join-Path $root "src/$Project"
    $exe = Join-Path $projectDir "bin/Debug/net10.0/$Project$(if ($IsLinux -or $IsMacOS) { '' } else { '.exe' })"
    $log = Join-Path $logs "$Name.log"
    $err = Join-Path $logs "$Name.err.log"
    # The apphost itself: its process id is the service's. The working directory is the project's, as `dotnet run` would use, because
    # settings such as the watcher's ../../local-inbox are relative to it.
    $parameters = @{
        FilePath = $exe; WorkingDirectory = $projectDir; RedirectStandardOutput = $log; RedirectStandardError = $err
        WindowStyle = 'Hidden'; PassThru = $true
    }
    # Start-Process refuses an empty argument list.
    if ($Arguments.Count -gt 0) { $parameters.ArgumentList = $Arguments }
    $p = Start-Process @parameters
    $script:started += $p
    Add-Content -Path $pidFile -Value $p.Id
    Write-Host ("Started {0,-8} (pid {1}), log: {2}" -f $Name, $p.Id, $log)
}

function Start-Npm([string]$Name, [string]$WorkingDirectory) {
    $log = Join-Path $logs "$Name.log"
    $err = Join-Path $logs "$Name.err.log"
    $p = Start-Process -FilePath 'npm.cmd' -ArgumentList @('start') -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $log -RedirectStandardError $err -WindowStyle Hidden -PassThru
    $script:started += $p
    Add-Content -Path $pidFile -Value $p.Id
    Write-Host ("Started {0,-8} (pid {1}), log: {2}" -f $Name, $p.Id, $log)
}

function Wait-Healthy([string]$Name, [string]$Url, [int]$Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            if ((Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2).StatusCode -lt 400) { return }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    throw "$Name did not become ready at $Url. See $logs."
}

try {
    # Development turns on the dev sign-in (ADR 0009): Authorization: Bearer dev-handler or dev-senior for a person, and the
    # services use their own dev tokens between themselves. None of this works outside the Development environment.
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:DOTNET_ENVIRONMENT = 'Development'   # The inbox watcher is a generic host, which reads this one.
    $env:Telemetry__Exporter = $Exporter      # console: a span per stage and one trace id from the watcher to the API.

    Start-Service 'legacy' 'ClaimsIntake.Legacy' @('--urls', 'http://localhost:5200')
    Start-Service 'api' 'ClaimsIntake.Api' @('--urls', 'http://localhost:5100')
    Wait-Healthy 'legacy' 'http://localhost:5200/health/live'
    Wait-Healthy 'api' 'http://localhost:5100/health/ready'

    # The watcher's appsettings.json points at ../../local-inbox, which is the folder above.
    Start-Service 'watcher' 'ClaimsIntake.Watcher' @()

    if (-not $NoUi) {
        $ui = Join-Path $root 'ui'
        if (-not (Test-Path (Join-Path $ui 'node_modules'))) {
            Write-Host 'Installing UI packages...'
            Push-Location $ui
            try { npm ci --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' } } finally { Pop-Location }
        }
        Start-Npm 'ui' $ui
        Wait-Healthy 'ui' 'http://localhost:4200/' 120
    }

    Write-Host ''
    Write-Host 'Everything is running.'
    Write-Host '  Review UI : http://localhost:4200'
    Write-Host '  Review API: http://localhost:5100/claims?status=all   (needs a token: curl -H "Authorization: Bearer dev-handler" ...)'
    Write-Host '  API docs  : http://localhost:5100/openapi/v1.json     (Development only)'
    Write-Host '  Dev users : dev-handler (ClaimsHandler), dev-senior (SeniorHandler). Development only.'
    Write-Host '  Legacy    : http://localhost:5200/policies/TM-CP-100001'
    Write-Host "  Inbox     : $inbox"
    Write-Host '  Try       : Copy-Item samples/01-water-damage-clean.pdf local-inbox/'
    Write-Host 'Press Ctrl+C to stop. If this window is killed instead, run: scripts/run-local.ps1 -Stop'

    if ($ExitAfterSeconds -gt 0) {
        Start-Sleep -Seconds $ExitAfterSeconds
    } else {
        while ($true) { Start-Sleep -Seconds 1 }
    }
}
finally {
    Write-Host 'Stopping...'
    Stop-Everything
}
