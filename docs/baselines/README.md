# Baselines

Committed reference runs. Each is three repetitions through the bench harness,
reported as median with the observed range, alongside the commit, host and target
configuration that produced it.

| file | tool | profile | scale | commit |
| --- | --- | --- | --- | --- |
| [nbomber-load.md](nbomber-load.md) | NBomber 6.6.0 | `load` | 1.0 | `f88475f` |
| [k6-load.md](k6-load.md) | k6 2.1.0 | `load` | 1.0 | `f88475f` |

The `.json` beside each is the machine-readable form, for comparing a later run
against this one.

## What is here and what is not

Only the `load` profile has a committed baseline. It is the one that gates CI, the
one where both suites run the same closed model, and therefore the only one where
a cross-tool number means anything without qualification.

The capacity, stress, spike, endurance and correlation figures quoted in
[../findings.md](../findings.md) are single runs, labelled there as `n=1`. They
are quoted because their effects are order-of-magnitude — a tenfold ceiling
collapse, a 136x latency increase after a spike — not because a single run is
sufficient evidence for a few percent.

Committing baselines for those shapes is deliberately deferred. The capacity
ladders alone take about five minutes per repetition per tool, and the endurance
profile eighteen; doing them properly is an overnight job rather than something
to squeeze in beside development.

## Reading the cross-tool comparison

At full scale, three repetitions each, on the same commit:

| scenario | NBomber p50 | k6 p50 | difference |
| --- | --- | --- | --- |
| pooled queue | 51.65ms [51.58–51.68] | 51.76ms [51.71–52.04] | 0.21% |
| lock contention | 16.09ms [16.09–16.10] | 15.86ms [15.78–15.86] | 1.4% |
| baseline | 1.62ms [1.58–1.64] | 1.73ms [1.71–2.09] | 6.8% |
| n+1, slow step | 16.83ms [16.30–17.14] | 17.58ms [17.32–21.48] | 4.5% |

Two things worth noticing.

Agreement is tightest exactly where it should be. The queue-bound endpoints are
dominated by a server-side wait that neither tool influences, so both measure the
same thing to within a fifth of a percent. The fast endpoints are dominated by
per-request overhead in the generator, so they diverge more.

k6 was the noisier of the two on the fast endpoints, and the harness flagged it:
`baseline` throughput ranged 666 to 787 rps across three runs against NBomber's
863 to 893. Neither tool is wrong; a JavaScript runtime per virtual user simply
has more variance than a compiled one. It is a reason to prefer the slower,
server-bound scenarios when comparing tools, and a reason not to read a 5%
difference on a 1.6ms endpoint as meaningful.
