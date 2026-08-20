# perf-testing-lab

Load, stress, spike and endurance testing of the same .NET service, written
twice: once in **NBomber** (C#) and once in **k6** (JavaScript).

Most load-testing examples point at a public demo API, which makes them
useless for their stated purpose — a healthy remote service under modest load
produces a flat line, and a flat line teaches nothing. This repository ships
its own system under test: an ASP.NET Core API backed by a real PostgreSQL
instance, with specific performance pathologies built in on purpose. Every
test type here exists to expose one of them.

Because both suites drive the identical target under identical conditions,
the two sets of results are directly comparable — which is the point.

## Licensing, up front

**NBomber is proprietary and free for personal use only.** Organisational use
requires a paid Business or Enterprise licence; versions 4 and earlier were
Apache-2.0, version 5 onward is closed-source. The NBomber suite here is a
personal-use project. Read
[the NBomber licence](https://nbomber.com/docs/getting-started/license/)
before adopting any of it at work.

**k6 is AGPL-3.0** and free to self-host, commercially included.

That difference is a real input to a tooling decision, so it is treated as
one in [docs/tool-comparison.md](docs/tool-comparison.md) rather than left as
a footnote.

## The system under test

A minimal ASP.NET Core API where each route is a deliberate failure mode,
individually toggleable so a test can isolate one variable:

| Pathology | Mechanism | Test type it exposes |
| --- | --- | --- |
| Connection pool exhaustion | Npgsql pool capped well below offered concurrency | Capacity |
| N+1 query | One query per row instead of a join | Load |
| Unbounded cache | Per-request key retained forever | **Endurance** |
| Lock contention | Single global lock on a hot path | Stress |
| Slow dependency, no timeout | Downstream call with no cancellation | Breakpoint |
| Rate limiting | Returns 429 above a threshold | Spike |
| Allocation pressure | Large short-lived allocations | Endurance |
| Cold start | No warm-up on first request | All |

The container runs with pinned `cpus` and `mem_limit`. Without a fixed
resource ceiling the measured breaking point moves with whatever else the host
is doing, and no two runs can be compared.

## Test types

| Type | Shape of load | Question it answers |
| --- | --- | --- |
| Smoke | 1 user, 1 minute | Is the script itself correct? |
| Load | Steady at expected peak | Does it hold at the load we expect? |
| Capacity | Stepped ramp | Where is the knee? |
| Stress | Past the knee until failure | *How* does it fail? |
| Spike | 0 → peak → 0, instantly | Does it recover? |
| Endurance | Moderate load, hours | What degrades only with time? |
| Breakpoint | Ramp until SLOs breach | What is the hard ceiling? |

## Layout

```
src/PerfLab.Sut/          ASP.NET Core system under test
tests/PerfLab.Sut.Tests/  Testcontainers guard: assert each pathology
                          misbehaves as designed before spending hours on it
nbomber/                  NBomber suite (C#) — scenarios, profiles, thresholds
k6/                       k6 suite (JavaScript) — mirrors nbomber/ deliberately
results/baselines/        Committed reference runs
docs/                     Methodology and tool comparison
```

Testcontainers is used for the correctness tests and CI smoke tier only —
never for the measured runs. Perf measurement needs a warmed, resource-pinned,
long-lived target, and a container whose lifecycle is owned by the test
process is the opposite of that. The reasoning is in
[docs/methodology.md](docs/methodology.md).

## Status

Built in phases; this section tracks what actually runs today.

- [x] Repository scaffolding and build configuration
- [x] System under test and Docker Compose environment
- [x] Testcontainers correctness guard
- [x] NBomber suite
- [ ] k6 suite
- [ ] InfluxDB + Grafana, committed baselines
- [ ] CI pipelines and methodology docs

Quick-start instructions and measured results land with the phases that make
them true. No numbers are published here until they come from a real run.
