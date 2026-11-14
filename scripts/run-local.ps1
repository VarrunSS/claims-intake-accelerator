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

    Output of each process goes to .local-run/<name>.log.

.PARAMETER NoUi
    Do not start the Angular dev server.

.PARAMETER ExitAfterSeconds
    Stop after this many seconds instead of waiting for Ctrl+C. Useful for smoke tests.

.EXAMPLE
    ./scripts/run-local.ps1
    Copy-Item samples/01-water-damage-clean.pdf local-inbox/
#>
[CmdletBinding()]
param(
    [switch]$NoUi,
    [int]$ExitAfterSeconds = 0
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$logs = Join-Path $root '.local-run'
$inbox = Join-Path $root 'local-inbox'
New-Item -ItemType Directory -Force -Path $logs, $inbox | Out-Null

Write-Host 'Building...'
dotnet build (Join-Path $root 'ClaimsIntake.sln') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }

$started = @()

function Start-Service([string]$Name, [string]$File, [string[]]$Arguments, [string]$WorkingDirectory) {
    $log = Join-Path $logs "$Name.log"
    $err = Join-Path $logs "$Name.err.log"
    $p = Start-Process -FilePath $File -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $log -RedirectStandardError $err -WindowStyle Hidden -PassThru
    $script:started += $p
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
    $env:Telemetry__Exporter = 'none'   # Set to console to see a span per pipeline stage.

    Start-Service 'legacy' 'dotnet' @('run', '--no-build', '--project', 'src/ClaimsIntake.Legacy', '--urls', 'http://localhost:5200') $root
    Start-Service 'api' 'dotnet' @('run', '--no-build', '--project', 'src/ClaimsIntake.Api', '--urls', 'http://localhost:5100') $root
    Wait-Healthy 'legacy' 'http://localhost:5200/health/live'
    Wait-Healthy 'api' 'http://localhost:5100/health/ready'

    # The watcher's appsettings.json points at ../../local-inbox, which is the folder above.
    Start-Service 'watcher' 'dotnet' @('run', '--no-build', '--project', 'src/ClaimsIntake.Watcher') $root

    if (-not $NoUi) {
        $ui = Join-Path $root 'ui'
        if (-not (Test-Path (Join-Path $ui 'node_modules'))) {
            Write-Host 'Installing UI packages...'
            Push-Location $ui
            try { npm ci --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' } } finally { Pop-Location }
        }
        Start-Service 'ui' 'npm.cmd' @('start') $ui
        Wait-Healthy 'ui' 'http://localhost:4200/' 120
    }

    Write-Host ''
    Write-Host 'Everything is running.'
    Write-Host '  Review UI : http://localhost:4200'
    Write-Host '  Review API: http://localhost:5100/claims?status=all   (needs a token: curl -H "Authorization: Bearer dev-handler" ...)'
    Write-Host '  Dev users : dev-handler (ClaimsHandler), dev-senior (SeniorHandler). Development only.'
    Write-Host '  Legacy    : http://localhost:5200/policies/TM-CP-100001'
    Write-Host "  Inbox     : $inbox"
    Write-Host '  Try       : Copy-Item samples/01-water-damage-clean.pdf local-inbox/'
    Write-Host 'Press Ctrl+C to stop.'

    if ($ExitAfterSeconds -gt 0) {
        Start-Sleep -Seconds $ExitAfterSeconds
    } else {
        while ($true) { Start-Sleep -Seconds 1 }
    }
}
finally {
    Write-Host 'Stopping...'
    foreach ($p in $started) {
        if (-not $p.HasExited) { & taskkill.exe /PID $p.Id /T /F *> $null }
    }
}
