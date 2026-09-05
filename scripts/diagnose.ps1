# Diagnose Encomm environment
#
# Collects safe diagnostic information about the local environment
# without revealing secrets, browsing history, or personal data. The
# output is intended to be attached to a bug report.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/diagnose.ps1

[CmdletBinding()]
param(
    [string]$Output = "diagnose.txt"
)

$ErrorActionPreference = 'Continue'

$lines = @()
function Out($msg) { $script:lines += $msg; Write-Host $msg }

Out "=== Encomm AI Browser diagnostic report ==="
Out "Generated: $(Get-Date -Format o)"
Out "User: $env:USERNAME"
Out ""

Out "--- OS ---"
Out "Caption:           $((Get-CimInstance Win32_OperatingSystem).Caption)"
Out "Version:           $((Get-CimInstance Win32_OperatingSystem).Version)"
Out "Architecture:      $env:PROCESSOR_ARCHITECTURE"
Out "Processors:        $([Environment]::ProcessorCount)"
Out ""

Out "--- .NET ---"
try { Out "dotnet --version:    $((dotnet --version) 2>$null)" } catch { Out "dotnet: not found" }
try {
    $sdks = (dotnet --list-sdks 2>$null)
    if ($sdks) { Out "dotnet --list-sdks:"; $sdks | ForEach-Object { Out "  $_" } }
} catch {}
try {
    $runtimes = (dotnet --list-runtimes 2>$null)
    Out "dotnet --list-runtimes (win):"
    if ($runtimes) {
        $runtimes | Where-Object { $_ -match 'WindowsDesktop' -or $_ -match 'win' } | ForEach-Object { Out "  $_" }
    }
} catch {}
Out ""

Out "--- WebView2 runtime ---"
try {
    $regPaths = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
    )
    foreach ($p in $regPaths) {
        if (Test-Path $p) {
            $v = (Get-ItemProperty $p -ErrorAction SilentlyContinue).pv
            if ($v) { Out "WebView2 runtime version: $v (from $p)"; break }
        }
    }
    $probe = "C:\Program Files (x86)\Microsoft\EdgeWebView\Application"
    if (Test-Path $probe) {
        $dirs = Get-ChildItem $probe -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
        if ($dirs) { Out "WebView2 install dir: $probe\$($dirs[0].Name)" }
    }
}
catch {
    Out "WebView2 probe failed: $($_.Exception.Message)"
}
Out ""

Out "--- Windows App Runtime packages ---"
try {
    $pkgs = Get-AppxPackage -AllUsers 2>$null | Where-Object { $_.Name -like 'Microsoft.WindowsAppRuntime*' } | Sort-Object Version
    foreach ($p in $pkgs) {
        Out "  $($p.Name) v$($p.Version) [$($p.PackageUserInformation)]"
    }
}
catch {
    Out "Get-AppxPackage failed: $($_.Exception.Message)"
}
Out ""

Out "--- Encomm build ---"
$root = (Get-Item "$PSScriptRoot\..").FullName
Out "Repo root:          $root"
$encomm = Join-Path $root "src\Encomm.Browser.App\Encomm.Browser.App.csproj"
Out "Encomm app csproj:  $encomm"
if (Test-Path $encomm) {
    try {
        $content = Get-Content $encomm -Raw
        $ver = ($content | Select-String -Pattern 'Microsoft\.WindowsAppSDK.*Version="([^"]+)"').Matches[0].Groups[1].Value
        $wv  = ($content | Select-String -Pattern 'Microsoft\.Web\.WebView2.*Version="([^"]+)"').Matches[0].Groups[1].Value
        Out "WindowsAppSDK: $ver"
        Out "WebView2:        $wv"
    } catch { Out "Could not parse versions" }
}
Out ""

Out "--- Encomm logs (last 50 lines) ---"
$log = Join-Path $env:LOCALAPPDATA "Encomm\Encomm-AI-Browser\Logs\encomm.log"
if (Test-Path $log) {
    Get-Content $log -Tail 50 | ForEach-Object { Out "  $_" }
} else {
    Out "  (no log file at $log)"
}
Out ""

Out "--- Tools ---"
try { Out "git --version:  $((git --version) 2>$null)" } catch {}
try { Out "node --version: $((node --version) 2>$null)" } catch {}

Out ""
Out "=== End of report ==="

$lines -join "`n" | Out-File -FilePath $Output -Encoding utf8
Write-Host ""
Write-Host "Wrote $Output"
