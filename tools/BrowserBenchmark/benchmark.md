# Encomm Browser Benchmark - Phase 2A

- OS: Microsoft Windows NT 10.0.19045.0
- CPU cores: 32
- Runtime: 9.0.17

| Tabs | Creation (ms) | After Create Host WS | After Create Host Private | After Active Host WS | After Active Host Private | Total | Live | Warm | Ghost |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 4 | 37.29 MB | 10 MB | 37.29 MB | 10 MB | 0 | 0 | 0 | 0 |
| 10 | 1 | 37.14 MB | 9.75 MB | 37.14 MB | 9.75 MB | 0 | 0 | 0 | 0 |
| 25 | 2 | 37.27 MB | 9.87 MB | 37.27 MB | 9.87 MB | 0 | 0 | 0 | 0 |
| 50 | 4 | 37.51 MB | 10.07 MB | 37.51 MB | 10.07 MB | 0 | 0 | 0 | 0 |
| 100 | 7 | 38.68 MB | 11.28 MB | 38.68 MB | 11.28 MB | 0 | 0 | 0 | 0 |

Numbers are REAL working-set / private-bytes for the host process. The
logical-tab count tracks the tab domain. This is NOT a WebView2
renderer benchmark; that requires a running Encomm process and
is scheduled for Phase 2B.
