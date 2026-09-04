# Run script for Encomm-AI-Browser.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/run.ps1
#
# Note: this launches the app as an unpackaged WinUI 3 desktop app.
# It requires the Windows App SDK 1.7 runtime to be installed.

[CmdletBinding()]
param(
    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item "$PSScriptRoot\..").FullName

$exe = Join-Path $root "src\Encomm.Browser.App\bin\$Configuration\net9.0-windows10.0.19041.0\win-x64\Encomm.exe"
if (-not (Test-Path $exe)) {
    Write-Host "Build output not found. Building first..." -ForegroundColor Yellow
    & powershell -ExecutionPolicy Bypass -File "$PSScriptRoot\build.ps1" -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

Write-Host "==> Launching $exe" -ForegroundColor Cyan
& $exe
if ($LASTEXITCODE -ne 0) {
    Write-Host "App exited with code $LASTEXITCODE" -ForegroundColor Yellow
}