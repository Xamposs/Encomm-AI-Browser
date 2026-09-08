# Serve the local Encomm benchmark pages.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/BenchmarkSite/serve.ps1 [-Port 8099]
#
# Serves tools/BenchmarkSite over http://127.0.0.1:<port>/ so the
# in-app renderer benchmark can navigate to repeatable local pages:
#   /static.html /js.html /images.html /form.html /dynamic.html
#
# Falls back to file:// URLs when python is unavailable.

[CmdletBinding()]
param([int]$Port = 8099)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command py -ErrorAction SilentlyContinue }
if ($python) {
    Write-Host "Serving $root at http://127.0.0.1:$Port/ (Ctrl+C to stop)"
    & $python.Source -m http.server $Port --bind 127.0.0.1 --directory $root
} else {
    Write-Host "python not found. Use file:// URLs instead, e.g.:"
    Write-Host "  file:///$($root.Replace('\','/'))/static.html"
}
