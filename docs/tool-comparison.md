# NBomber vs k6

Both suites in this repository drive the same target through the same shapes and
emit the same result schema. This is what that produced.

Versions: NBomber 6.6.0 on .NET 10, k6 2.1.0. Evidence in
[findings.md](findings.md), baselines in [baselines/](baselines/).

---

## Verdict first

**For most teams, k6.** Not because it measures better — the two agree to 0.02%
where it counts — but because it is AGPL-3.0 and free to self-host commercially,
while NBomber is proprietary and free for personal use only. That decides it
before any technical argument is reached.

**Choose NBomber when** the scenarios need real .NET: reusing production client
libraries, driving a non-HTTP protocol through an existing SDK, or sharing domain
code with the system under test. Writing that in JavaScript means reimplementing
it. If your organisation will pay the licence, this is a genuine reason.

**Choose k6 when** the tests are HTTP or gRPC, the team is polyglot, or the
results need to stream somewhere live. Also when nobody wants to own a licence
conversation.

---

## Licensing and cost

The section most comparisons omit, and the one that usually decides.

| | NBomber | k6 |
| --- | --- | --- |
| Licence | Proprietary | AGPL-3.0 |
| Free for personal use | Yes | Yes |
| Free for organisational use | **No** | Yes |
| Self-host commercially | Requires Business or Enterprise licence | Yes |
| Last open-source version | 4.x (Apache-2.0) | current |
| Reminder at runtime | Prints a warning after every run | None |

NBomber v5 onward is closed-source. The runner prints
`THIS VERSION IS FREE ONLY FOR PERSONAL USE. You can't use it for an
organization.` after every run, so this is not a licence subtlety anyone can
plead ignorance of.

Practical consequence: skills built on current NBomber do not transfer to work
without a purchase. Skills built on v4 transfer but are three major versions
stale. This repository is a personal-use project for exactly that reason.

---

## Where they agree — and this is the important part

Closed model, same virtual user counts, three repetitions each, same commit:

| scenario | NBomber p50 | k6 p50 | delta |
| --- | --- | --- | --- |
| pooled queue | 51.65ms [51.58–51.68] | 51.76ms [51.71–52.04] | **0.21%** |
| lock contention | 16.09ms [16.09–16.10] | 15.86ms [15.78–15.86] | 1.4% |
| baseline | 1.62ms [1.58–1.64] | 1.73ms [1.71–2.09] | 6.8% |

Two different languages, two HTTP clients, two schedulers, and the queue-bound
endpoint agrees to a fifth of a percent. Both tools also independently measured
the resource-holding dependency's ceiling at **exactly 10 rps**, the value
derived from `pool 20 / 2s latency`.

**Neither tool is a source of measurement error worth worrying about.** Pick on
everything else.

Agreement is tightest where a server-side wait dominates and loosest where
per-request generator overhead does — which is also the guidance: compare tools
on server-bound scenarios, and do not read a 5% difference on a 1.6ms endpoint as
meaningful.

---

## Where they differ

### Open-model accounting — the biggest difference

k6 reports the rate it **achieved** and drops iterations it cannot start.
NBomber's `Inject` guarantees injection and reports the offered rate.

| endpoint | k6 plateau | NBomber reported | derived ceiling |
| --- | --- | --- | --- |
| pool | ~420 rps | full offered rate at every rung | 400 |
| lock | ~145 rps | full offered rate at every rung | 154 |

So in k6 the knee is visible in throughput; in NBomber it is only visible in
latency. This cuts both ways:

- **k6 is better for finding capacity.** The plateau lands within a few percent
  of the ceiling derived from first principles.
- **NBomber is better for spike damage.** Because k6 drops what it cannot
  deliver, it builds a smaller backlog and understates the consequence: k6
  showed p50 recovering fully after a burst while p95 stayed at 618ms, where
  NBomber showed p50 itself still at 7323ms forty seconds later.

They model different clients. NBomber's guaranteed injection represents callers
that do not back off, which is the more realistic assumption for public traffic
and the more pessimistic one. **Read the achieved rate before believing any
recovery figure from k6.**

### Queueing below saturation

At identical offered rates well under capacity, the two disagree by a factor of
ten:

| endpoint | offered | k6 mean | NBomber mean |
| --- | --- | --- | --- |
| pool | 300 rps | 51.72ms | 89.45ms |
| lock | 100 rps | 8.24ms | 81.05ms |

Both agree closely under a closed model, so this is specific to arrival-rate
generation. The likely cause is arrival **burstiness** — queue waiting depends on
the variability of arrivals, not only their mean — but it has not been isolated,
so it is an open question rather than a conclusion.

**Consequence regardless of cause:** a knee measured with one tool is not purely
a property of the service. If a capacity number is going to be quoted, the
generator belongs in the quote.

### Run-to-run noise

k6 is the noisier tool on fast endpoints. Across three runs its `baseline`
throughput ranged 666–787 rps against NBomber's 863–893, and two k6 rows
exceeded the harness's 10% noise threshold while no NBomber row did.

A JavaScript runtime per virtual user has more variance than a compiled one. It
does not matter for server-bound scenarios and it does matter if you intend to
detect small regressions on cheap endpoints.

---

## Feature by feature

| | NBomber | k6 |
| --- | --- | --- |
| Per-scenario and per-step stats | **Native** | Build it yourself with custom metrics |
| Warm-up | **`WithWarmUpDuration`, samples discarded** | None; separate scenario plus `startTime` |
| Built-in reports | **HTML, Markdown, CSV, text** | `handleSummary` only; write your own |
| Custom metrics in the summary | Registered but never appear | **Work, including per-step** |
| Thresholds on server-side metrics | Impossible | **Just another threshold** |
| Threshold on a step | Separate `Threshold.Create` overload | Same as any metric |
| Rendered threshold expression | **Yes, in the report** | Metric name plus expression |
| VU allocation for arrival rate | Automatic | **Must be declared** |
| Counter rate semantics | Per scenario | Whole test — a trap |
| Test data abstraction | `DataFeed` **removed in v6** | Never had one |
| Live metrics | Sink broken; InfluxDB alternative | **Prometheus remote write, native** |
| Distributed execution | Cluster, Enterprise licence | Grafana Cloud or self-managed |
| Language | C# / F# | JavaScript |

### The ones that cost real time

**k6's per-scenario metrics.** k6 aggregates by metric, so `http_req_duration` is
one distribution across everything a run did. Getting NBomber's per-scenario and
per-step breakdown meant a Trend and two Counters per scenario, declared at init
because k6 forbids creating metrics inside an iteration. Roughly 100 lines that
NBomber gives away.

**NBomber's custom metrics do not reach the summary.** `Metric.CreateGauge` and
`CreateCounter`, tested both inside and outside the scenario body, appeared in
neither `NodeStats.Metrics` nor any of the four report formats — only NBomber's
own eleven process gauges did, and those describe the *generator*, not the
target. They appear to target reporting sinks. Server-side observation had to
fall back to plain static state.

**k6 can threshold a server-side measurement; NBomber cannot.** Because the
observer's samples are ordinary k6 metrics, the endurance heap budget is a
threshold like any other and fails the run through the same mechanism as a
latency objective. On the NBomber side it had to live outside the threshold
system entirely, as a property checked by hand after the run, because no
expression over `ScenarioStats` can reach a number the client never saw.

**k6's Counter rate is computed over the whole test duration.** A scenario offset
behind a warm-up reports a rate diluted by the time it sat idle: `pooledQueue`
showed 73.9/s on a 28-second test when its own 18-second window carried 115.2/s,
and a `rate>=90` threshold failed a service comfortably meeting it. Throughput
objectives have to be expressed as counts over a known window. NBomber has no
equivalent trap.

**k6 forces you to confront VU allocation.** An arrival-rate executor needs
`preAllocatedVUs` and `maxVUs`, and by Little's Law the requirement is rate ×
latency — so generating deep overload needs thousands of VUs precisely when the
service is slowest. NBomber allocates silently, which is more convenient and less
honest: the same limit exists there, and NBomber's lock ladder hit it as
generator saturation producing self-contradictory numbers.

**NBomber's sink ecosystem lags its core.** `NBomber.Sinks.Prometheus 1.0.0` does
not load against NBomber 6.6.0 at all — `TypeLoadException: Method 'Start' does
not have an implementation`, compiled against an older `IReportingSink` — and it
resolves `OpenTelemetry.Api 1.6.0-alpha.1`, which carries advisory
GHSA-g94r-2vxg-569j. k6 ships Prometheus remote write in the box and it worked
first time.

---

## Harness capability

On the same 22-core host, against a trivial endpoint:

| | sustained | latency | collapse behaviour |
| --- | --- | --- | --- |
| NBomber | 10,000 rps | 6.65ms | strain from 12,000 |
| k6 | ~7,800 rps | 57.71ms at 8,000 | 10,000 offered delivered 1,457 with 13,041 failures |

NBomber generates more load per host. Both become untrustworthy above their
ceiling — k6's 14,000 rung delivered 7,655 with zero failures after 10,000 and
12,000 had collapsed, which is the signature of a harness rather than a
measurement.

Rarely decisive: both ceilings sit an order of magnitude above the pathology
ceilings being measured.

---

## Ergonomics

**NBomber** gives you compile-time checking, refactoring, debugger support and
four report formats for free. Load simulations name the workload model
explicitly — `KeepConstant` versus `Inject` — which is genuinely educational: it
forces the open/closed decision into the code where a reader sees it, where k6's
`constant-vus` versus `constant-arrival-rate` is the same distinction hidden in
an executor name.

**k6** starts faster, has no build step, and a scenario body is a handful of
lines. Its scenario map with `startTime` and `exec` is more expressive than
NBomber's `Simulation.Pause` staggering for building multi-phase shapes. The
documentation is better.

Both need the same amount of care to produce a trustworthy number. Neither
protects you from measuring the wrong thing.

---

## What this comparison does not cover

- **One host, one target.** No distributed execution, no cloud, no multi-region.
- **HTTP only.** k6 has browser and gRPC modules; NBomber is protocol-agnostic
  by design. Neither claim was tested.
- **A single .NET target.** A tool's ergonomics depend on what it is pointed at.
- **NBomber's paid features.** Studio, Cluster and the Kubernetes integration
  were not evaluated, because evaluating them requires a licence.
- **Long-run stability.** Nothing here ran longer than about twenty minutes.
- **Team factors.** Onboarding cost, CI integration in a real pipeline, and who
  ends up maintaining the tests matter more than most of the above and are not
  measurable here.
