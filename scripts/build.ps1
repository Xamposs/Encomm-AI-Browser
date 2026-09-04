# Build script for Encomm-AI-Browser.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/build.ps1 -Configuration Debug
#   powershell -ExecutionPolicy Bypass -File scripts/build.ps1 -Configuration Release

[CmdletBinding()]
param(
    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item "$PSScriptRoot\..").FullName
Set-Location $root

Write-Host "==> Restoring..." -ForegroundColor Cyan
& dotnet restore src/Encomm.Browser.App/Encomm.Browser.App.csproj
if ($LASTEXITCODE -ne 0) { throw "Restore failed." }

Write-Host "==> Building ($Configuration)..." -ForegroundColor Cyan
& dotnet build src/Encomm.Browser.App/Encomm.Browser.App.csproj -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Write-Host "==> Done." -ForegroundColor Green