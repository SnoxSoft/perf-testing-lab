# Methodology

How this repository measures things, and why each choice is made that way. The
evidence for these rules is in [findings.md](findings.md); this is the distilled
version.

## Choose the workload model deliberately

| | Closed | Open |
| --- | --- | --- |
| k6 | `constant-vus`, `ramping-vus` | `constant-arrival-rate` |
| NBomber | `KeepConstant`, `RampingConstant` | `Inject`, `RampingInject` |
| Mechanism | fixed workers loop request → response → request | fixed arrival rate regardless of completion |
| When the service slows 2× | throughput halves, the service sees **less** load | rate unchanged, the queue grows |
| Models | a bounded client, a fixed worker pool | real users, public traffic |
| Cannot show | saturation, a death spiral | — |

A closed model politely backs off exactly when production would pile on. That is
why load tests miss outages, and why capacity, stress, spike and endurance here
all use the open model.

The closed model has a second limitation that is easy to miss: **throughput is an
output, not an input.** One virtual user on a 1ms endpoint generates more traffic
than six on a 50ms endpoint, so a closed model cannot express "60% catalog reads,
30% reports, 10% reservations" at all. If a traffic mix matters, the model has to
be open.

## Establish the harness ceiling before anything else

Run a trivial endpoint — no database, no locks, no allocation — as a ladder, and
find where the generator itself gives up. Every other number is read against
that figure.

Any endpoint measured within an order of magnitude of the ceiling is partly
reporting the harness. Non-monotonic latency as offered load increases is the
signal that you have left the trustworthy range.

Signs of being generator-bound rather than target-bound:

- throughput plateaus while target CPU is idle
- **latency stays flat as concurrency increases** — a saturated target's latency climbs
- generator CPU pegged
- adding a second generator raises total throughput

## Pin the target's resources

`cpus` and `mem_limit` are the most important lines in the Compose file. Without
a fixed ceiling the measured breaking point drifts with whatever else the host is
doing, no two runs are comparable, and regression detection — the main reason to
put performance tests in CI — becomes impossible.

Then **verify utilisation during the run**:

```bash
docker stats --no-stream --format "{{.CPUPerc}}" perflab-sut-1
```

Read `cpus: 2.0` as a 200% limit. 40–60% is a steady state. Above ~85% every
scenario is measuring the CPU and you will attribute it to whichever pathology
you were looking at.

## One variable per run

A synthetic maximum-throughput scenario cannot be a co-tenant of the mix whose
latency you intend to trust — it consumes the headroom it was added to prove.

Database-backed endpoints share a connection pool, so their capacities are not
independent. Endpoint capacity measured in isolation does not add up, and a load
test of one endpoint at a time can pass while the combined mix fails. Size
concurrency against the shared budget, and use a scenario filter when you need
attributable single-endpoint numbers.

## Ladders, not continuous ramps, for finding a knee

A continuous ramp smears every arrival rate together, so a p99 from the run
cannot be attributed to any particular rate. Discrete plateaus give one stable
(rate, latency, throughput) triple per rung, which is what a knee actually is.

A ladder should **bracket** the ceiling, not vanish over it. Past collapse an
open model spawns work faster than either side can retire it, the generator
saturates, and the numbers describe the harness — in one case reporting "ok"
latencies above the client timeout, which cannot be true of a request that did
not time out. The useful data is at the knee and just past it.

Reserve continuous ramps for shapes where the *transition* is the subject: spike
recovery, breakpoint hunting.

## Derive the expected answer first

The best capacity test is one whose answer you can compute beforehand:

```
pool ceiling = pool size / hold time      = 20 / 0.05s = 400 rps
lock ceiling = 1 / critical section       = 1 / 0.0065s ≈ 154 rps
holding dep. = pool size / dep. latency   = 20 / 2s     = 10 rps
```

If the measurement does not land near the derivation, the test is wrong before
the system is. All three were confirmed to within a few percent, and the third to
the exact request.

Note the distinction the arithmetic does not give you: the **saturation point**
is where throughput stops, and the **latency knee** is materially lower, because
queue waiting grows as ρ/(1−ρ) and blows up well before utilisation reaches one.
Your SLO cares about the knee.

## Little's Law, with the mean

```
L = λ × W        concurrency = throughput × latency
```

Use it to size a test beforehand and to check one afterwards. `L ≈ configured
concurrency` is a free correctness check on any closed-model run.

**It requires the mean, not p50.** Percentiles are for service level objectives;
the mean is for capacity arithmetic. Substituting p50 on a right-skewed
distribution produced a 35% error.

## Warm-up

Warm-up absorbs JIT, connection pool fill and cache population. Without it, one
cold sample in ten *is* p95 — a 130ms JIT outlier against a 1.6ms p50.

- NBomber has `WithWarmUpDuration`, which discards its samples automatically.
- k6 has no equivalent: warm-up must be a separate scenario with the measured
  ones offset behind it by `startTime`.
- NBomber's per-scenario warm-up runs at t=0 for **every** scenario in parallel,
  so on a staggered ladder it pollutes the rung being measured. Use
  `WithoutWarmUp` plus one explicit warm-up scenario there.

**Never scale warm-up down.** JIT and pool fill are fixed costs that do not
shrink because the measured window did.

## Thresholds are budgets, not baselines

| | SLO threshold | Regression detection |
| --- | --- | --- |
| States | a requirement | a trend |
| Fails when | users would notice | anything changed |
| Lives in | the gate | baseline comparison |

Two different jobs. A gate that fails on noise is disabled within a fortnight.

Derive budgets from **service time**, not from observed latency. A p50 budget of
70ms against a 54ms service time means "requests are waiting for a connection
rather than using one". A budget of "2× the observed p50" expresses nothing.

Always report how many objectives were evaluated, including on a clean run. **A
threshold that never fires is indistinguishable from one that cannot fire**, and
a gate silently reduced to zero checks still exits 0. Keep a deliberately failing
profile and run it in CI as an expected failure: it is a regression test on the
gate itself.

A latency objective is not optional. Availability and throughput objectives
cannot detect saturation.

## Measure what the server sees, not only what the client felt

Client latency describes the symptom. It cannot distinguish a service that is
slow because work is queued from one that is slow because the heap is growing or
because downstream calls are accumulating — different faults, different fixes,
identical latency curves.

Sample the target's own diagnostics as a **scenario**, at a low rate, against an
endpoint that touches nothing. Same scheduler, same report, same timeline as the
load that caused it. An observer that perturbs what it measures is worse than
none.

Track **peaks, not final values**: a run ends after the load stops, so the last
sample is taken while the system is already recovering.

## Memory

Extrapolate **resident memory**, not the managed heap. Cgroups kill on RSS, and
RSS grows faster than the heap because the collector reserves more than it holds.
Using the heap understated time-to-limit by a third.

A growth rate is not a finding until it is expressed as time remaining:

```
minutes to limit = (limit − current RSS) / RSS growth per minute
```

Memory experiments need **serialising and isolating**. Heap is one shared number,
so arms cannot overlap — and sequential is not enough either, because the second
arm runs on a heap the first one inflated. A true control needs a fresh process
per arm. Latency experiments only need serialising.

Do not assume a leak shows up as GC pauses. At moderate scale the cost is the
data structure itself; pauses arrive later. A run long enough to *show* growth
is not necessarily long enough to *characterise* the failure.

## Duration is a variable in its own right

A soak reduced to one aggregate p99 has discarded the only dimension it was
measuring. Split the run into windows registered as separate scenarios, so the
summary becomes a latency-over-time table. The finding is never "p99 was 8ms" —
it is "the last window was 27% worse than the first".

Scale the shape, not the numbers: one duration multiplier keeps window counts and
proportions identical, so trends stay comparable. Only compare like-scale runs,
because absolute throughput shifts with run length.

## Test data

Three patterns, genuinely different meanings:

| pattern | indexed by | use when | reproducible |
| --- | --- | --- | --- |
| circular | iteration number | each request should look like a distinct user | yes |
| per virtual user | VU id | the identity carries session state | yes |
| random | RNG | realism, exploration | **no** |

Random is disqualifying for a committed baseline: two runs draw different
sequences, so a difference between them can be the data rather than the code.

The data set must be **larger than the virtual user count**, or it silently
becomes shared state and manufactures cache hits a real population would not
produce.

Reusing one cache key is the most common way a load test proves nothing — it
measures the cache rather than the system, and any leak stays invisible.

## Correlation

Authentication is expensive by design in every real system and validation is not.
A test that authenticates per iteration spends most of its budget on the login
path and reports the endpoint under test as far slower than it is — measured
here as exactly half the useful throughput.

1. Cache the token **per virtual user**, not per iteration.
2. Refresh **before** expiry, with a margin. A token valid when checked can
   expire in flight, producing intermittent 401s that look like service faults.
3. Confirm the model is closed. Per-VU caching relies on per-VU state, which an
   arrival-rate executor does not preserve — under NBomber's `Inject` every
   arrival is a fresh instance and the cache silently degenerates with nothing in
   the output to say so.
4. **Assert the response belongs to the caller.** Otherwise a test where every VU
   shares one token passes happily and its hit ratio is fiction.
5. Put think time **outside** the measured step, so objectives sit on step
   latency and do not move when pacing changes. A scenario with no pacing
   measures a load pattern nobody has.

## Repeat, and record provenance

A single run finds defects. It cannot distinguish a regression from the moment a
background process woke up.

- **A fresh process per repetition**, always. JIT state, connection pools and any
  static state in the suite carry over otherwise, so the first run
  systematically differs.
- **Restart the target** for profiles whose result depends on accumulated server
  state.
- **Health-check before every run.** A run against a stopped target completes
  happily and produces transport errors that look exactly like catastrophic
  regression.
- **Median with the observed range**, not mean and standard deviation. At three
  to five repetitions a standard deviation is not meaningful, and the median is
  not dragged by one unlucky run. Flag rows whose spread exceeds ~10% of the
  median — quoting a single number for those misleads.

Record with every result: the commit and whether the tree was dirty, the scale
factor, host CPU count, the target's resource limits, and the target's own
configuration payload. **A latency comparison between two differently configured
runs is not a comparison**, and without the configuration recorded there is no
way to know afterwards which was which.

## Know what a suspicious number looks like

- A latency that resembles a round platform timeout — 1s, 21s, 30s, 75s — is
  infrastructure, not your system. Real service latency is rarely a suspiciously
  round number.
- A stress run that fails **fast** is a wiring problem. Real saturation fails
  slowly.
- Non-monotonic results as load increases mean you have left the measurable
  range.
- A tool reporting exactly the offered rate past saturation is telling you about
  its own accounting, not the service's capacity. Read the *achieved* rate.
