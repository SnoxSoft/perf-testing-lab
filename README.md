# perf-testing-lab

Load, stress, spike and endurance testing of a .NET service, against a target
built to misbehave on purpose.

Most load-testing examples point at a public demo API, which makes them useless
for their stated purpose: a healthy remote service under modest load produces a
flat line, and a flat line teaches nothing. You cannot find a knee, characterise
a failure mode, or run a soak against a target that never breaks.

So this repository ships its own target — an ASP.NET Core API backed by real
PostgreSQL, with specific performance pathologies built in, running under a
pinned CPU and memory limit. Every load shape exists to expose one of them.
Because the target is controlled, a prediction can be made before a run and
checked against it afterwards, which is the only way to know the method works.

The same suite is written twice, in NBomber and in k6, driving the same target
through the same shapes and emitting the same result schema — so the two are
directly comparable rather than compared by eye.

Three documents carry the substance:

- **[docs/findings.md](docs/findings.md)** — fifteen findings with the numbers
  behind them, and what each means for a service that is not this one.
- **[docs/methodology.md](docs/methodology.md)** — the rules those findings
  produced, and how to measure so that a result is worth believing.
- **[docs/tool-comparison.md](docs/tool-comparison.md)** — NBomber against k6 on
  everything actually encountered, licensing included.

## Licensing, up front

**NBomber is proprietary and free for personal use only.** Organisational use
requires a paid Business or Enterprise licence, and the runner prints that
warning after every run. Versions 4 and earlier were Apache-2.0; version 5
onward is closed-source. This repository is a personal-use project. Read
[the NBomber licence](https://nbomber.com/docs/getting-started/license/) before
adopting any of it at work.

**k6 is AGPL-3.0** and free to self-host, commercially included.

That difference is a real input to a tooling decision, so it is treated as one in
[docs/tool-comparison.md](docs/tool-comparison.md) rather than left as a footnote.

## Quick start

Requires .NET 10 SDK and Docker.

```bash
docker compose up -d --build
```

Wait for both containers to report healthy, then run a load shape:

```bash
cd nbomber/PerfLab.NBomber
dotnet run -- smoke
```

Reports are written to `nbomber/PerfLab.NBomber/reports/<profile>/` as HTML,
Markdown, CSV and text. Run with no valid profile name to list them all.

Useful switches:

```bash
PERFLAB_SCALE=0.5 dotnet run -- load        # halve every duration, same shape
dotnet run -- load --scenario=pooled_queue  # isolate one scenario
PERFLAB_SUT_URL=http://127.0.0.1:8080 dotnet run -- load
```

After changing anything under `src/`, rebuild the container — `dotnet build`
alone does not update it:

```bash
docker compose up -d --build
```

### Measuring properly

A single run finds defects; it does not make a baseline. The bench harness runs a
profile several times and reports the median with the observed range, plus the
commit, host and target configuration that produced it:

```bash
dotnet run --project tools/PerfLab.Bench -- load --repeats=3
dotnet run --project tools/PerfLab.Bench -- load --repeats=3 --tool=k6
```

It uses a fresh process per repetition, restarts the target for profiles whose
result depends on accumulated server state, and refuses to start if the target
is unhealthy rather than recording a run full of transport errors. Output lands
in `results/<tool>/<profile>/` as `summary.json` and `summary.md`.

Only compare like-scale runs — absolute throughput shifts with run length, which
is why the scale factor is recorded in every result.

### Watching a run live (optional)

```bash
docker compose --profile observability up -d
k6 run --out experimental-prometheus-rw --env PROFILE=endurance k6/main.js
```

Grafana on http://localhost:3000, Prometheus on http://localhost:9090. Off by
default: it adds two containers competing with the target and the generator, and
the measured runs do not need it. Its value is watching the long shapes unfold —
the moment a spike's backlog starts draining, or a soak's heap turning over.

This is **k6 only**. NBomber's Prometheus sink does not work with NBomber 6.6.0
(`TypeLoadException`), and its InfluxDB sink would mean a second time series
database for one tool. See [observability/prometheus.yml](observability/prometheus.yml)
for the detail.

### Running the tests

```bash
dotnet run --project tests/PerfLab.Sut.Tests
```

Seventeen Testcontainers-backed tests assert that every pathology still
misbehaves as designed. Run them before a long soak: an endurance run against
an endpoint whose cache quietly started evicting produces a clean, flat and
entirely meaningless graph.

> **Known issue:** `dotnet test` reports "Zero tests ran" with exit code 5,
> while the identical binary launched as above discovers and passes all 17.
> Configuration has been ruled out; this appears to be an incompatibility
> between xunit.v3 4.0.0's Microsoft.Testing.Platform adapter and the .NET 10
> SDK's new `dotnet test` mode. Use the command above.

## The system under test

Each route is a deliberate failure mode, individually configurable so a run
changes one variable at a time.

| Pathology | Mechanism | Exposed by |
| --- | --- | --- |
| Connection pool exhaustion | Npgsql pool of 20, held 50ms per request | `capacity-pool` |
| N+1 query | 26 queries for a result set one join could return | `load` |
| Unbounded cache | ~8KB retained per distinct key, forever | `endurance` |
| Lock contention | Serialised 5ms critical section | `capacity-lock` |
| Untimed dependency | 2s downstream call, no cancellation | `stress` (control) |
| Resource-holding dependency | Same 2s call, holds a pooled connection | `stress` (collapses) |
| Rate limiting | Genuine 429s above 50 req/s | `spike` |
| Allocation pressure | 256KB per request, above the LOH threshold | `endurance` |
| Cold start | 1s penalty on the first request only | all (warm-up) |
| Expensive authentication | 25ms issuance, cheap validation | `correlation` |

The container runs with pinned `cpus` and `mem_limit`. Without a fixed ceiling
the measured breaking point drifts with whatever else the host is doing, and no
two runs are comparable — which makes regression detection, the main reason to
put performance tests in CI, impossible.

## Load shapes

| Profile | Shape | Question it answers |
| --- | --- | --- |
| `smoke` | 1 user, fixed iterations | Does the test work? (Not the system.) |
| `load` | `KeepConstant`, closed model | At expected peak, do we meet our SLOs? |
| `capacity-pool` | Stepped `Inject` ladder | Where is the knee for a 20-server queue? |
| `capacity-lock` | Stepped `Inject` ladder | Where is the knee for a serialised one? |
| `ceiling` | Ladder on a trivial endpoint | What can the harness itself measure? |
| `stress` | Two arms past capacity | How does it fail? |
| `spike` | before / burst / after | Does it recover, and how fast? |
| `endurance` | Modest load, windowed | What degrades only with time? |
| `correlation` | Token per iteration vs per user | What does bad correlation cost? |
| `slo-breach` | Deliberately over capacity | Does the gate actually fire? |

`slo-breach` is expected to fail, and is kept precisely for that reason: a
threshold that never fires is indistinguishable from one that cannot fire.

## Layout

```
src/PerfLab.Sut/          ASP.NET Core system under test
tests/PerfLab.Sut.Tests/  Testcontainers guard: assert each pathology
                          misbehaves as designed before spending hours on it
nbomber/PerfLab.NBomber/  NBomber suite — scenarios, profiles, thresholds
k6/                       k6 suite — deliberately mirrors nbomber/ file for file
tools/PerfLab.Results/    The run schema both suites emit, so results aggregate
tools/PerfLab.Bench/      Repeat runner: median, range and provenance
docs/                     Findings, methodology and the tool comparison
```

Testcontainers is used for the correctness tests only, never for the measured
runs. Asserting behaviour is happy with a cold, randomly ported, freshly started
database; asserting timing needs a warmed, resource-pinned, long-lived target,
which is what the Compose environment provides.

## Status

Built in phases; this tracks what actually runs today.

- [x] Repository scaffolding and build configuration
- [x] System under test and Docker Compose environment
- [x] Testcontainers correctness guard
- [x] NBomber suite — 10 profiles
- [x] Repeat-run harness reporting median and spread
- [x] k6 suite mirroring the NBomber one — 9 profiles, same schema, same harness
- [x] Prometheus and Grafana as an optional compose profile (k6 only)
- [x] Findings, methodology and a committed baseline for the load profile
- [x] CI pipelines — correctness on pull requests, shapes nightly

**The load profile has a committed baseline; the other shapes do not.** See
[docs/baselines/](docs/baselines/) for what is measured and why the rest is
single-run — the capacity ladders take about five minutes per repetition per
tool and endurance eighteen, so baselining them properly is an overnight job.

The reproducibility is good where it has been measured: across three runs of the
load profile the queue-bound endpoints held their p50 to within 0.2%, and the two
suites agreed with each other to 0.21%. Absolute throughput shifts with run
length, so only like-scale runs are comparable.
