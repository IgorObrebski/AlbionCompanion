<#
.SYNOPSIS
Rebuilds AlbionCompanion.Service and redeploys it over the already-installed
copy at %ProgramData%\AlbionCompanion\service\, without touching the Windows
Service registration (name, ACLs, binPath) that AlbionCompanion.ServiceInstaller
sets up once.

Use this after any code change under AlbionCompanion.Service (or anything it
depends on, e.g. AlbionCompanion.Gathering/Sniffer/Core) during development.
Re-run the full installer instead only if the registration itself needs to
change (service name, install path, ACL grants).

Must be run elevated (stopping/starting a Windows Service requires it).
#>

$ErrorActionPreference = "Stop"

$ServiceName = "AlbionCompanionService"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$InstallPath = Join-Path $env:ProgramData "AlbionCompanion\service"
$PublishPath = Join-Path $env:TEMP "AlbionCompanionServicePublish"

function Write-Step($message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($currentUser)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "This script must be run as Administrator (stopping/starting a Windows Service requires it)." -ForegroundColor Red
    exit 1
}

Write-Step "Stopping $ServiceName (if running)"
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
    $service.WaitForStatus('Stopped', (New-TimeSpan -Seconds 15))
} else {
    Write-Host "Service not running or not installed yet - continuing."
}

Write-Step "Publishing AlbionCompanion.Service"
if (Test-Path $PublishPath) {
    Remove-Item -Recurse -Force $PublishPath
}
$serviceProject = Join-Path $RepoRoot "AlbionCompanion.Service\AlbionCompanion.Service.csproj"
dotnet publish $serviceProject -c Debug -r win-x64 --self-contained true -o $PublishPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed - service left stopped, old binaries still in place. Fix the build error and re-run." -ForegroundColor Red
    exit 1
}

Write-Step "Copying published output to $InstallPath"
if (-not (Test-Path $InstallPath)) {
    Write-Host "$InstallPath does not exist yet - run AlbionCompanion.ServiceInstaller once first to register the service." -ForegroundColor Red
    exit 1
}
Copy-Item -Path (Join-Path $PublishPath "*") -Destination $InstallPath -Recurse -Force

Write-Step "Starting $ServiceName"
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', (New-TimeSpan -Seconds 15))

Write-Step "Done. $ServiceName is running the freshly built code."
