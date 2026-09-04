# Test script for Encomm-AI-Browser.
#
# Runs the unit-test project (xUnit) and reports results.

[CmdletBinding()]
param(
    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'
$root = (Get-Item "$PSScriptRoot\..").FullName
Set-Location $root

Write-Host "==> Building tests ($Configuration)..." -ForegroundColor Cyan
& dotnet build tests/Encomm.Browser.Tests/Encomm.Browser.Tests.csproj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Test build failed." }

Write-Host "==> Running tests..." -ForegroundColor Cyan
& dotnet test tests/Encomm.Browser.Tests/Encomm.Browser.Tests.csproj -c $Configuration --no-build --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }

Write-Host "==> Tests passed." -ForegroundColor Green