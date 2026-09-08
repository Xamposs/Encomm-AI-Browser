# Encomm Browser Benchmark - Phase 2B

- OS: Microsoft Windows NT 10.0.19045.0
- CPU cores: 32
- Runtime: 9.0.17

| Tabs | Creation (ms) | Host WS | Host Private | WV2 Browser | WV2 Renderer | WV2 GPU | WV2 Utility | Tree Total | Live | Warm | Ghost |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 4 | 37.39 MB | 10.56 MB | 0 B | 0 B | 0 B | 0 B | 37.39 MB | 0 | 0 | 0 |
| 5 | 0 | 37.38 MB | 10.38 MB | 0 B | 0 B | 0 B | 0 B | 37.38 MB | 0 | 0 | 0 |
| 10 | 1 | 37.46 MB | 10.51 MB | 0 B | 0 B | 0 B | 0 B | 37.46 MB | 0 | 0 | 0 |
| 25 | 2 | 37.62 MB | 10.63 MB | 0 B | 0 B | 0 B | 0 B | 37.62 MB | 0 | 0 | 0 |
| 50 | 5 | 38.84 MB | 10.93 MB | 0 B | 0 B | 0 B | 0 B | 38.84 MB | 0 | 0 | 0 |
| 100 | 9 | 39.42 MB | 11.44 MB | 0 B | 0 B | 0 B | 0 B | 39.42 MB | 0 | 0 | 0 |

Numbers are REAL working-set / private-bytes for the host process. The
logical-tab count tracks the tab domain. This is NOT a WebView2
renderer benchmark; that requires a running Encomm process and
is scheduled for Phase 2B+ in the running-app in-process mode.
