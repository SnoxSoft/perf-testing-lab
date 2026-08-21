# load (k6)

3 runs at scale 1, same target throughout. Outcomes: Passed, Passed, Passed.

Median with [min–max] across runs. A flagged row means the runs disagreed by more
than 10% of the median, so the median should not be quoted on its own.

| scenario | rps | mean ms | p50 ms | p95 ms | p99 ms | fails | |
| --- | --- | --- | --- | --- | --- | --- | --- |
| warmup | 92.70 [92.10–93.00] | 21.41 [21.32–21.55] | 8.32 [8.32–8.56] | 53.40 [53.19–53.48] | 54.30 [54.15–54.36] | 0 |  |
| baseline | 775.78 [666.30–787.47] | 2.40 [2.36–2.77] | 1.73 [1.71–2.09] | 3.28 [3.27–4.16] | 24.16 [24.12–24.86] | 0 | ⚠ noisy |
| nPlusOne | 227.83 [199.60–232.57] | 12.98 [12.71–14.79] | 13.62 [12.69–13.74] | 43.50 [42.86–46.47] | 49.53 [48.48–53.27] | 0 | ⚠ noisy |
| &nbsp;&nbsp;↳ one_query | 113.92 [99.80–116.28] | 2.08 [1.97–2.40] | 1.73 [1.70–2.09] | 3.01 [2.99–3.67] | 6.25 [4.58–9.23] | 0 | ⚠ noisy |
| &nbsp;&nbsp;↳ n_queries | 113.92 [99.80–116.28] | 23.88 [23.44–27.19] | 17.58 [17.32–21.48] | 45.77 [45.06–49.32] | 57.49 [55.60–61.05] | 0 | ⚠ noisy |
| pooledQueue | 112.57 [111.52–112.78] | 53.11 [53.00–53.53] | 51.76 [51.71–52.04] | 69.17 [65.76–69.22] | 76.63 [76.20–76.73] | 0 |  |
| lockContention | 124.85 [124.70–124.88] | 15.79 [15.73–15.81] | 15.86 [15.78–15.86] | 16.98 [16.92–17.07] | 18.32 [18.02–18.69] | 0 |  |

## Provenance

- commit `f88475f`
- tool: k6 2.1.0
- host: Microsoft Windows 10.0.26200, X64, 22 logical cores
- target limits: 2 CPU, 1024 MB
- collected 2026-08-21T09:43:07.1820364+00:00

Target configuration at the time of the run:

```json
{"maxPoolSize":20,"pooledHoldDuration":"00:00:00.0500000","nPlusOneRowCount":25,"unboundedCache":true,"slowDependencyLatency":"00:00:02","searchRateLimitPerSecond":50,"exportAllocationBytes":262144,"coldStartPenalty":"00:00:01","tokenIssuanceCost":"00:00:00.0250000","tokenLifetime":"00:01:00"}
```
