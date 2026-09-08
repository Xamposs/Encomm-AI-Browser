# Package the Encomm AI Browser Release output for distribution testing.
#
# Copies the self-contained Release build into
# artifacts/Encomm-AI-Browser-win-x64/ so it can be zipped or copied to
# another Windows 11 machine with the documented prerequisites
# (WebView2 Runtime; see docs/DEPLOYMENT.md).
#
# Build output itself is NOT committed; the artifacts/ directory is
# git-ignored. This script only stages a local folder.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/package.ps1 [-Configuration Release]

[CmdletBinding()]
param(
    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item "$PSScriptRoot\..").FullName

Write-Host "==> Building ($Configuration)..." -ForegroundColor Cyan
& dotnet build "$root\src\Encomm.Browser.App\Encomm.Browser.App.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$src = Join-Path $root "src\Encomm.Browser.App\bin\$Configuration\net9.0-windows10.0.19041.0\win-x64"
if (-not (Test-Path (Join-Path $src "Encomm.exe"))) { throw "Encomm.exe not found in $src" }

$dst = Join-Path $root "artifacts\Encomm-AI-Browser-win-x64"
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
New-Item -ItemType Directory -Path $dst | Out-Null
Copy-Item (Join-Path $src "*") $dst -Recurse -Force

Write-Host "==> Staged: $dst" -ForegroundColor Green
Get-ChildItem $dst | Select-Object Name | Select-Object -First 8
Write-Host ""
Write-Host "Prerequisites on the target machine: WebView2 Runtime (see docs/DEPLOYMENT.md)."
