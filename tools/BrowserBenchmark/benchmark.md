# Encomm Browser Benchmark - Phase 2A

- OS: Microsoft Windows NT 10.0.19045.0
- CPU cores: 32
- Runtime: 9.0.17

| Tabs | Creation (ms) | After Create Host WS | After Create Host Private | After Active Host WS | After Active Host Private | Total | Live | Warm | Ghost |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 4 | 37.75 MB | 10.01 MB | 37.75 MB | 10.01 MB | 0 | 0 | 0 | 0 |
| 10 | 1 | 37.61 MB | 9.77 MB | 37.61 MB | 9.77 MB | 0 | 0 | 0 | 0 |
| 25 | 2 | 37.76 MB | 9.89 MB | 37.76 MB | 9.89 MB | 0 | 0 | 0 | 0 |
| 50 | 4 | 38.07 MB | 10.08 MB | 38.07 MB | 10.08 MB | 0 | 0 | 0 | 0 |
| 100 | 8 | 38.47 MB | 10.59 MB | 38.47 MB | 10.59 MB | 0 | 0 | 0 | 0 |

Numbers are REAL working-set / private-bytes for the host process. The
logical-tab count tracks the tab domain. This is NOT a WebView2
renderer benchmark; that requires a running Encomm process and
is scheduled for Phase 2B.
