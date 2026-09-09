# Compare a benchmark result against the regression baseline (Phase 2C item 22).
#
# Informational only: flags meaningful regressions but always exits 0.
#   powershell -ExecutionPolicy Bypass -File scripts/compare-benchmark.ps1 -Current artifacts/bench-H-....json [-Baseline benchmarks/baseline-win-x64.json]
#
# Flags: +15% process-tree memory, +20% restore latency, renderer-count mismatch.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Current,
    [string]$Baseline = ""
)

$ErrorActionPreference = 'Stop'
$repo = (Get-Location).Path
if ($Baseline -eq "") { $Baseline = Join-Path $repo "benchmarks\baseline-win-x64.json" }
if (-not (Test-Path $Baseline)) { Write-Warning "Baseline not found: $Baseline"; exit 0 }
if (-not (Test-Path $Current)) { throw "Current result not found: $Current" }

$base = Get-Content $Baseline -Raw | ConvertFrom-Json
$cur = Get-Content $Current -Raw | ConvertFrom-Json

$flags = @()
foreach ($cs in $cur.Samples) {
    $match = @($base.scenarios | Where-Object { $_.id -eq $cs.Id })
    if ($match.Count -eq 0) { continue }
    $bs = $match[0]
    if ($bs.treeMedian -gt 0) {
        $delta = ($cs.Snapshot.ProcessTreeBytes - $bs.treeMedian) / $bs.treeMedian
        if ($delta -ge 0.15) {
            $flags += "$($cs.Id): tree +{0:P0} over baseline ({1:0} MB vs {2:0} MB)" -f $delta, ($cs.Snapshot.ProcessTreeBytes / 1MB), ($bs.treeMedian / 1MB)
        }
    }
    if ($bs.viewCount -ne $cs.ViewCount) {
        $flags += "$($cs.Id): renderer count $($cs.ViewCount) vs baseline $($bs.viewCount)"
    }
}
if ($null -ne $cur.Restores -and $null -ne $base.restoreTotalMedianMs -and $base.restoreTotalMedianMs -gt 0) {
    $delta = ($cur.Restores.TotalMedianMs - $base.restoreTotalMedianMs) / $base.restoreTotalMedianMs
    if ($delta -ge 0.20) {
        $flags += "restore: total median +{0:P0} over baseline" -f $delta
    }
}
if ($flags.Count -eq 0) {
    Write-Host "compare-benchmark: no regressions flagged."
} else {
    Write-Host "compare-benchmark: INFORMATIONAL flags:"
    $flags | ForEach-Object { Write-Host "  - $_" }
}
exit 0
