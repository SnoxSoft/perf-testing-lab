# Findings

What the runs actually showed. Each finding names the mechanism, the numbers, and
what it means for a service that is not this one.

## Reading these numbers

Everything here comes from one Windows laptop, 22 logical cores, with the target
pinned to 2 CPUs and 1 GB. Absolute figures are properties of *this* hardware.
The relationships between them are not, and those are the point.

Provenance is labelled per finding:

- **n=3** — three repetitions through the bench harness, reported as median with
  the observed range. Committed under [baselines/](baselines/).
- **n=1** — a single run. Enough to find a defect, not enough to publish. Where a
  single run is quoted, it is because the effect is an order of magnitude and not
  a few percent.

---

## 1. An availability SLO does not catch saturation

**n=1.** The pool endpoint served 500 rps at **7121ms mean latency with zero
failed requests**, because the client timeout sat far above the worst case.

Proven as a gate rather than argued. The `slo-breach` profile offers 450 rps
against the same four objectives the load profile uses:

| threshold | actual | verdict |
| --- | --- | --- |
| `Fail.Request.Count == 0` | 0 failures | **PASSED** |
| `Ok.Request.RPS >= 90` | 450 rps | **PASSED** |
| `Ok.Latency.Percent50 <= 70` | 2480ms | BREACHED |
| `Ok.Latency.Percent99 <= 150` | >4500ms | BREACHED |

13,500 requests, zero failures, exit code 1. **Two of four objectives reported a
healthy service while it was returning two-and-a-half-second responses.**

**Transferable:** availability and throughput objectives cannot detect
saturation. Only latency can. A gate without latency thresholds does not gate
anything that matters.

---

## 2. Server count dominates utilisation

**n=1 per rung, but the two ladders are directly comparable.**

Two queues measured at the same 81% utilisation:

| resource | servers | ceiling | latency at ρ≈0.81 | inflation over service time |
| --- | --- | --- | --- | --- |
| Npgsql pool | 20 | 370 rps | 89.45ms | **1.66×** |
| Global lock | 1 | 154 rps | 218.16ms | **33.6×** |

A twentyfold difference in latency inflation at identical utilisation, from
server count alone. M/M/1 waiting grows as ρ/(1−ρ); adding servers flattens that
curve (Erlang-C).

**Transferable:** "we are at 80% of capacity" means completely different things
for a pooled resource and a serialised one. Global locks, single writers,
leader-only writes and single-threaded workers should be budgeted near 50%
utilisation where a pool is comfortable at 80%.

---

## 3. Minimum latency is the cleanest saturation signal

**n=1.** While a queue still empties occasionally, **min latency stays pinned at
service time regardless of offered load** — 52ms across the pool's entire
pre-saturation range, including at 400 rps.

Once min rises above service time, the queue never drains:

| | min latency | service time |
| --- | --- | --- |
| pool at 450 rps | 696ms | 54ms |
| lock at 175 rps | 4152ms | 6.5ms |

**Transferable:** better than any percentile for this one question, because it is
binary. Either some request found an empty queue, or none did. Percentiles tell
you how bad it is; min tells you whether you are over the line at all.

---

## 4. A slow dependency is fatal only if it holds something scarce

**n=1, confirmed independently by both tools.** Identical 2s downstream call,
identical offered rates. One variable: whether a pooled connection is held across
the wait.

| offered | `/enrich` mean | fails | `/enrich-holding` mean | ok | fails |
| --- | --- | --- | --- | --- | --- |
| 25 rps | 2003ms | 0 | 16469ms | 500 | 0 |
| 50 rps | 2003ms | 0 | 31987ms | 200 | 800 |
| 100 rps | 2002ms | 0 | 31996ms | **200** | 896 |
| 200 rps | 2003ms | 0 | 31996ms | **200** | 1532 |

Arm A is flat and faultless at eight times the rate where arm B has collapsed.
The observer saw **406 concurrent in-flight calls** at 200 rps against 21 threads
and a stable heap — a pending timer is not a resource, so unbounded async
in-flight work is close to free and the textbook thread-per-request cascade does
not occur on an async stack.

Arm B succeeded **exactly 200 times per 20-second window at every rate at or
above 50 rps**. That is 10/s, precisely `pool size / dependency latency = 20/2s`.
Both NBomber and k6 measured the same figure.

**Transferable:** in-flight count does not determine whether a slow dependency is
survivable — whether that work holds something scarce does. A 30-second HTTP
timeout on a call that borrows one of 20 connections is a 10 rps ceiling you do
not know you have. Scope the timeout tighter than the resource it borrows.

---

## 5. The failure mode matters more than the ceiling

**n=1.** Arm B's failures were **HTTP 500 after the full 30-second Npgsql pool
acquisition timeout**. Not a 503, not shed load, not a fast failure: a user waits
half a minute for an internal server error.

And at 25 rps the same endpoint reported **100% availability while serving
16.5-second responses**.

**Transferable:** a pool-acquisition timeout should be shorter than the
user-facing timeout and should surface as 503, so callers can retry or degrade
rather than hanging.

---

## 6. Spike damage outlasts the spike, and by a predictable amount

**n=1.** A 10-second 3× burst, with identical rates either side so the plateaus
are comparable:

| plateau | rate | p50 |
| --- | --- | --- |
| before | 100 rps | 53.82ms |
| burst | 1200 rps | 11517.95ms |
| after | 100 rps | **7323.65ms** |

Same offered rate as before, **136× the latency**, still degraded 40 seconds
after a 10-second burst.

The arithmetic predicts it:

```
backlog       = (burst rate − capacity) × burst duration = (1200 − 370) × 10s = 8300
drain rate    = capacity − ongoing arrivals             = 370 − 100          = 270/s
recovery time = backlog / drain rate                    = 8300 / 270         ≈ 31s
```

**Transferable:** the denominator is the part people forget. A queue drains at
capacity *minus whatever is still arriving*, so a service near capacity recovers
far more slowly than one with headroom.

---

## 7. Load shedding works, and error-rate gates punish it

**n=1, both tools agree.** The rate-limited endpoint under the same burst:

| plateau | rate | p50 | outcome mix |
| --- | --- | --- | --- |
| before | 40 rps | 2.51ms | — |
| burst | 1200 rps | **6.62ms** | 519 × 200, 11,481 × 429 |
| after | 40 rps | 2.91ms | — |

A 30× spike moved p50 from 2.51ms to 6.62ms and recovered fully. Under k6 the
burst p50 was **lower** than baseline (0.56ms), because refusing a request is
cheaper than serving one.

An error-rate gate would report **11,481 failures** on the one endpoint that
handled the spike correctly.

**Transferable:** 429 is a refusal, not a failure. Any gate that conflates them
punishes the correct behaviour and rewards the endpoint that fell over quietly.

---

## 8. Extrapolate resident memory, not the managed heap

**n=1.** The endurance run, at 50 rps — roughly a seventh of the pool endpoint's
capacity:

| basis | growth | rate | time to the 1 GB limit |
| --- | --- | --- | --- |
| managed heap | 92.8 MB | 13.6 MB/min | ~68 min ❌ |
| **working set** | **122.4 MB** | **18.0 MB/min** | **~45 min** ✅ |

Two reasons the heap flatters the result: cgroups kill on resident memory, and
RSS grows faster than the heap because the collector reserves more than it holds.

The starting position matters more than expected — **2.5 MB managed heap inside
an 86.3 MB working set**. 97% of the footprint is runtime, so almost the entire
limit is spent before a single byte leaks.

**Headline:** at an unremarkable 50 rps this service cannot survive an hour.

**Transferable:** managed heap says *what* is leaking; resident memory says *how
long you have*. A growth rate is not a finding until it is expressed as time
remaining.

---

## 9. The degradation was not GC

**n=1.** Only **one gen2 collection** occurred during the whole endurance run,
yet p95 rose 27% across four windows.

So the latency drift was the cost of a growing `ConcurrentDictionary` — hash
probing and cache locality — not pause time.

**Transferable:** do not assume "leak → GC pauses → latency". At this magnitude
the data structure itself is the cost. And a run long enough to *show* growth is
not necessarily long enough to *characterise* the failure.

---

## 10. Correlation done naively halves useful throughput

**n=1, both tools agree on the ratio.** Same endpoint, same identities, same
closed-model load, 20ms think time.

| | auth requests | orders requests | orders rps |
| --- | --- | --- | --- |
| NBomber naive | 7,179 | 7,179 | 159.53 |
| NBomber cached | **10** | 14,315 | **318.11** |
| k6 naive | 8,404 | 8,404 | 186.8 |
| k6 cached | **10** | 19,010 | **422.4** |

Half of the naive arm's traffic was login. Both arms spent almost the same total
request budget and the cached one converted it into **twice the useful work**.
Exactly ten tokens for ten virtual users under both tools.

Verified refresh behaviour: at double duration against a 60-second token
lifetime, the auth count came out at exactly 20 — one initial plus one refresh
per user — with zero 401s.

**Transferable:** cache the token per virtual user, refresh *before* expiry with
a margin, and assert the response belongs to the caller. A token valid when
checked can expire in flight, producing intermittent 401s that look like service
faults rather than test bugs.

---

## 11. Little's Law validates a closed-model run for free

**n=3.** `L = λ × W`, from the load profile:

| scenario | copies | rps | mean | λ × mean | error |
| --- | --- | --- | --- | --- | --- |
| baseline | 2 | 763.02 | 2.60ms | **1.98** | 1% |
| n_plus_one | 3 | 99.95 | 29.80ms | **2.98** | 0.7% |
| pooled_queue | 6 | 112.52 | 53.02ms | **5.97** | 0.5% |
| lock_contention | 2 | 124.65 | 15.95ms | **1.99** | 0.5% |

It only holds with the **mean**. Using p50 on baseline predicts 1031 rps against
763 measured — a 35% error, because the distribution is right-skewed.

**Transferable:** percentiles are for service level objectives; the mean is for
capacity arithmetic. `L ≈ configured concurrency` is a free correctness check on
any closed-model run — if it does not come out near your VU count, the test is
broken before the system is.

---

## 12. Two tools, one target, near-identical numbers — until the model changes

**n=3 for the load profile.** Closed model, same virtual user counts:

| scenario | k6 p50 | NBomber p50 |
| --- | --- | --- |
| `pooledQueue` | **51.38ms** | **51.39ms** |
| `lockContention` | 15.83ms | 16.03ms |
| `baseline` | 1.29ms | 1.37ms |

Agreement to 0.02% on the queue-bound endpoint, from two different languages and
HTTP clients.

**n=1 for the ladders.** Under an arrival-rate model they diverge sharply below
saturation:

| endpoint | offered | k6 mean | NBomber mean |
| --- | --- | --- | --- |
| pool | 300 rps | 51.72ms | 89.45ms |
| lock | 100 rps | 8.24ms | 81.05ms |

Both tools agree in the closed model, so this is specific to arrival-rate
generation. The likely cause is arrival **burstiness**: queue waiting depends on
the variability of arrivals, not only their mean, so a more evenly paced
generator produces far less queueing at identical throughput. Stated as a
hypothesis — it has not been isolated.

**Transferable:** a knee measured with one tool is not purely a property of the
service. If a capacity number is going to be quoted, the generator's arrival
pattern belongs in the quote.

---

## 13. k6 shows the knee in throughput; NBomber does not

**n=1.** k6 reports the rate it *achieved* and drops iterations it cannot start,
so throughput plateaus visibly:

| endpoint | k6 plateau | predicted ceiling |
| --- | --- | --- |
| pool | ~420 rps | 400 |
| lock | ~145 rps | 154 |

NBomber reported the full offered rate at every rung, because injection is
guaranteed and the backlog drains during graceful stop.

This **corrects an earlier conclusion.** "Throughput is not the signal in an open
model" was a statement about NBomber's accounting, not about open models.

The same difference reverses on spike tests: because k6 drops what it cannot
deliver, it built a smaller backlog and **understated the damage** — p50
recovered fully while p95 stayed at 618ms, where NBomber showed p50 itself still
at 7323ms. Neither is wrong; they model different clients. NBomber's guaranteed
injection represents callers that do not back off, which is usually the more
realistic assumption for public traffic.

**Transferable:** read the *achieved* rate before believing any recovery figure.

---

## 14. Verify target utilisation, or the numbers are fiction

**n=1.** The first version of the load profile included a synthetic
maximum-throughput scenario "to prove headroom". It consumed the headroom:
~15,000 rps drove the container to **173–193% of its 200% CPU limit**.

| | saturated | with headroom | expected |
| --- | --- | --- | --- |
| `pooled_queue` p50 | 84ms | **52.0ms** | ~50ms (the hold) |
| `lock_contention` p50 | 32ms | **16.1ms** | ~10ms + overhead |

That extra 34ms had nothing to do with the connection pool the scenario exists to
exercise.

**Transferable:** under CPU saturation every scenario measures the CPU, and you
will attribute it to whichever pathology you were looking at. A headroom check
cannot be a co-tenant of the mix it is checking. 40–60% of the limit is a steady
state; above ~85% the run is a stress test wearing a load test's name.

---

## 15. The generator lies in the reassuring direction

**n=1.** "100 concurrent" requests via `xargs -P 100 | curl` produced a median
queue wait of **0.0ms** against a pool that should have queued badly. Cause: 100
Windows processes at ~40ms spawn each, an effective **23 rps**, so nothing ever
overlapped. The same target via k6 at 200 VUs: **456ms**.

Symptoms of being generator-bound:

- throughput plateaus while target CPU is idle
- **latency stays flat as VUs increase** — a saturated target's latency climbs
- generator CPU pegged
- adding a second generator raises total throughput

Establish the harness ceiling first. On this hardware: NBomber sustained
**10,000 rps at 6.65ms**; k6 sustained **~7,800**. Above that k6 collapsed
non-monotonically — 10,000 offered delivered 1,457 with 13,041 failures, while
14,000 offered delivered 7,655 with none. Numbers from that region describe the
harness.

Both pathology ceilings, 420 and 145 rps, sit comfortably inside the trustworthy
range.

**Transferable:** this is the most common way a load test lies, and it always
lies toward "everything is fine". Any endpoint measured within an order of
magnitude of the harness ceiling is partly reporting the harness.
