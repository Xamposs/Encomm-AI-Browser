# Bootstrap script for Encomm-AI-Browser.
#
# Verifies prerequisites and prints clear instructions when something is
# missing. Idempotent. Safe to run repeatedly.
#
# Usage (from repo root):
#   powershell -ExecutionPolicy Bypass -File scripts/bootstrap.ps1

[CmdletBinding()]
param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

function Write-Section($title) {
    Write-Host ""
    Write-Host "==> $title" -ForegroundColor Cyan
}

function Test-Requirement {
    param([string]$Name, [scriptblock]$Check, [string]$InstallHint)
    $ok = & $Check
    if ($ok) {
        Write-Host ("  [OK]   {0}" -f $Name) -ForegroundColor Green
    }
    else {
        Write-Host ("  [MISS] {0}" -f $Name) -ForegroundColor Yellow
        if ($InstallHint) { Write-Host ("         Hint: {0}" -f $InstallHint) -ForegroundColor DarkYellow }
    }
    return $ok
}

Write-Section "Encomm-AI-Browser bootstrap"

$allOk = $true

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
$allOk = (Test-Requirement ".NET SDK 9.0" {
        if (-not $dotnet) { return $false }
        $v = (& dotnet --version 2>$null)
        return ($v -and $v.StartsWith("9."))
    } "Install .NET SDK 9.0 from https://dotnet.microsoft.com/download/dotnet/9.0") -and $allOk

$git = Get-Command git -ErrorAction SilentlyContinue
$allOk = (Test-Requirement "git" { return [bool]$git } "Install git for Windows from https://git-scm.com/download/win") -and $allOk

$wsroot = (Get-Item "$PSScriptRoot\..").FullName
$webview2 = "$env:LOCALAPPDATA\Encomm\Encomm-AI-Browser"
$webview2Exists = Test-Path $webview2
Write-Section "Prerequisites detected"
Write-Host ("  Repo root: {0}" -f $wsroot)
Write-Host ("  WebView2 user-data: {0} {1}" -f $webview2, ($(if($webview2Exists){"(exists)"} else {"(not yet created)"})))

Write-Section "NuGet sources"
& dotnet nuget list source | ForEach-Object { Write-Host "  $_" }

Write-Section "Stub MSBuild tasks"
$stub = Join-Path $wsroot "tools\StubTasks\Encomm.StubTasks.csproj"
if (Test-Path $stub) {
    $built = Join-Path $wsroot "tools\StubTasks\bin\Release\netstandard2.0\Encomm.Stub.MSBuildTasks.dll"
    if (Test-Path $built) {
        Write-Host "  [OK] $built" -ForegroundColor Green
    }
    else {
        Write-Host "  [..] Building..." -ForegroundColor Yellow
        & dotnet build $stub -c Release | Out-Null
        if (Test-Path $built) {
            Write-Host "  [OK] Built $built" -ForegroundColor Green
        }
        else {
            Write-Host "  [FAIL] Could not build stub tasks." -ForegroundColor Red
            $allOk = $false
        }
    }
}

Write-Section "Result"
if ($allOk) {
    Write-Host "All prerequisites satisfied. Run scripts\build.ps1 to compile." -ForegroundColor Green
}
else {
    Write-Host "Some prerequisites are missing. See hints above." -ForegroundColor Yellow
}