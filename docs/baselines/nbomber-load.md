# load (nbomber)

3 runs at scale 1, same target throughout. Outcomes: Passed, Passed, Passed.

Median with [min–max] across runs. A flagged row means the runs disagreed by more
than 10% of the median, so the median should not be quoted on its own.

| scenario | rps | mean ms | p50 ms | p95 ms | p99 ms | fails | |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 865.23 [863.18–892.73] | 2.29 [2.22–2.30] | 1.62 [1.58–1.64] | 2.63 [2.60–2.67] | 27.78 [27.31–28.27] | 0 |  |
| n_plus_one_comparison | 234.90 [230.80–241.13] | 12.68 [12.35–12.91] | 12.71 [12.52–13.02] | 45.50 [44.67–45.89] | 50.72 [49.98–50.75] | 0 |  |
| &nbsp;&nbsp;↳ one_query | 117.45 [115.40–120.57] | 1.93 [1.91–1.98] | 1.62 [1.60–1.65] | 2.54 [2.47–2.56] | 5.66 [4.14–6.16] | 0 |  |
| &nbsp;&nbsp;↳ n_queries | 117.45 [115.40–120.57] | 23.38 [22.78–23.91] | 16.83 [16.30–17.14] | 47.84 [46.98–47.84] | 57.38 [56.58–59.10] | 0 |  |
| pooled_queue | 112.87 [112.63–112.92] | 52.84 [52.84–52.97] | 51.65 [51.58–51.68] | 63.46 [59.52–63.97] | 79.10 [78.14–79.17] | 0 |  |
| lock_contention | 124.53 [124.50–124.72] | 15.97 [15.95–15.97] | 16.09 [16.09–16.10] | 16.83 [16.72–16.85] | 17.63 [17.17–17.90] | 0 |  |

## Provenance

- commit `f88475f`
- tool: nbomber 6.6.0
- host: Microsoft Windows 10.0.26200, X64, 22 logical cores
- target limits: 2 CPU, 1024 MB
- collected 2026-08-21T09:39:19.5241850+00:00

Target configuration at the time of the run:

```json
{"maxPoolSize":20,"pooledHoldDuration":"00:00:00.0500000","nPlusOneRowCount":25,"unboundedCache":true,"slowDependencyLatency":"00:00:02","searchRateLimitPerSecond":50,"exportAllocationBytes":262144,"coldStartPenalty":"00:00:01","tokenIssuanceCost":"00:00:00.0250000","tokenLifetime":"00:01:00"}
```
