# Run the Encomm isolated performance suite (Phase 2C items 6-7).
#
# Launches a FRESH Encomm process per scenario so later scenarios never
# inherit heap/cache/process state from earlier ones, collects the
# per-scenario JSON files from artifacts/, and aggregates medians into
# artifacts/performance-suite.json + .md.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/run-performance-suite.ps1 [-Iterations 3] [-Scenarios A,C,D,E,H]
#
# Default: A,C,D,E,H x3 iterations (the important scenarios) plus
# B,F,G,H1,H5,CREATE,RESTORE,WEB x1.

[CmdletBinding()]
param(
    [int]$Iterations = 3,
    [string]$Scenarios = "A,C,D,E,H"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not (Test-Path "$repo\src\Encomm.Browser.App\Encomm.Browser.App.csproj")) {
    # Fallback: assume PWD is the repo root.
    $repo = (Get-Location).Path
}
$exe = Join-Path $repo "src\Encomm.Browser.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\Encomm.exe"
$artifacts = Join-Path $repo "artifacts"
if (-not (Test-Path $exe)) { throw "Release exe not found: $exe. Build first." }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$priority = @($Scenarios.Split(',') | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ -ne '' })
$single = @('B', 'F', 'G', 'H1', 'H5', 'CREATE', 'RESTORE', 'WEB')

function Invoke-Scenario($id) {
    $before = Get-ChildItem $artifacts -Filter "bench-$id-*.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Write-Host "=== scenario $id (fresh process) ==="
    $p = Start-Process -FilePath $exe -ArgumentList "--run-bench=$id" -PassThru
    $exited = $p.WaitForExit(600000)
    if (-not $exited) {
        Write-Warning "Scenario $id timed out; killing."
        Stop-Process -InputObject $p -Force
        return $null
    }
    Write-Host "exit=$($p.ExitCode)"
    Start-Sleep -Seconds 3
    $after = Get-ChildItem $artifacts -Filter "bench-$id-*.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $after) { Write-Warning "No JSON for $id"; return $null }
    if ($null -ne $before -and $after.FullName -eq $before.FullName -and $after.LastWriteTime -eq $before.LastWriteTime) {
        Write-Warning "No NEW JSON for $id"
        return $null
    }
    return $after.FullName
}

$results = @{}
foreach ($id in $priority) {
    for ($i = 1; $i -le $Iterations; $i++) {
        Write-Host "--- $id iteration $i/$Iterations ---"
        $f = Invoke-Scenario $id
        if ($f) { $results[$id] += @($f) }
    }
}
foreach ($id in $single) {
    Write-Host "--- $id x1 ---"
    $f = Invoke-Scenario $id
    if ($f) { $results[$id] = @($f) }
}

$aggPath = Join-Path $artifacts "performance-suite.json"
$agg = [ordered]@{
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    iterations   = $Iterations
    scenarios    = [ordered]@{}
}
$bySample = @{}
foreach ($id in $results.Keys | Sort-Object) {
    foreach ($f in $results[$id]) {
        try {
            $j = Get-Content $f -Raw | ConvertFrom-Json
            foreach ($s in $j.Samples) {
                if (-not $bySample.ContainsKey($s.Id)) { $bySample[$s.Id] = @() }
                $bySample[$s.Id] += ,@{ tree = [long]$s.Snapshot.ProcessTreeBytes; valid = [bool]$s.ScenarioValid; file = $f }
            }
        } catch { Write-Warning "parse failed: $f" }
    }
}
foreach ($sid in $bySample.Keys | Sort-Object) {
    $rows = $bySample[$sid]
    $trees = @($rows | ForEach-Object { $_.tree } | Sort-Object)
    $med = if ($trees.Count -eq 0) { 0 } elseif ($trees.Count % 2 -eq 1) { $trees[[int]($trees.Count / 2)] } else { [long](($trees[$trees.Count / 2 - 1] + $trees[$trees.Count / 2]) / 2) }
    $agg.scenarios[$sid] = [ordered]@{
        runs          = $rows.Count
        allValid      = ($rows | Where-Object { -not $_.valid }).Count -eq 0
        treeMedian    = $med
        treeMin       = if ($trees.Count -eq 0) { 0 } else { $trees[0] }
        treeMax       = if ($trees.Count -eq 0) { 0 } else { $trees[-1] }
        files         = @($rows | ForEach-Object { $_['file'] } | Select-Object -Unique)
    }
}
($agg | ConvertTo-Json -Depth 6) | Set-Content $aggPath
Write-Host "Wrote $aggPath"

$md = @("# Encomm Performance Suite (isolated fresh-process runs)", "", "- Generated: $($agg.generatedUtc)", "- Iterations (priority scenarios): $Iterations", "")
$md += "| Scenario | Runs | All valid | Tree median | Tree min | Tree max |"
$md += "|---|---:|:---:|---:|---:|---:|"
foreach ($id in $agg.scenarios.Keys) {
    $s = $agg.scenarios[$id]
    $fmt = { param($b) if ($b -ge 1GB) { "{0:0.##} GB" -f ($b / 1GB) } else { "{0:0.##} MB" -f ($b / 1MB) } }
    $md += "| $id | $($s.runs) | $($s.allValid) | $(& $fmt $s.treeMedian) | $(& $fmt $s.treeMin) | $(& $fmt $s.treeMax) |"
}
$md += ""
$mdPath = Join-Path $artifacts "performance-suite.md"
$md | Set-Content $mdPath
Write-Host "Wrote $mdPath"
