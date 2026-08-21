import { defineProfile } from '../lib/profile.js';
import { WARMUP, dur, scaledSeconds } from '../lib/config.js';

/** The measured window, in seconds, used for both duration and threshold counts. */
const MEASURED_SECONDS = scaledSeconds(60);

/**
 * Steady state at expected peak, closed workload model.
 *
 * The mirror of the NBomber load profile, down to the virtual user counts, so a
 * difference in the results is a difference in the tools.
 *
 * Closed model means `constant-vus`: a fixed number of workers each looping
 * request, await response, request. Offered load self-throttles, so this shape
 * cannot produce a backlog — if you want to know what happens when arrivals
 * ignore your response times, a closed model will never tell you. That is what
 * the capacity and stress profiles are for.
 *
 * The virtual user counts are not arbitrary. Every database-backed scenario draws
 * on the same 20-connection Npgsql pool, and holding times differ by two orders
 * of magnitude — 50ms for pooledQueue, ~18ms for nPlusOne, ~1ms for baseline. At
 * these counts peak simultaneous demand is around 11 of 20. Raising any of them
 * turns a load test into an unintentional stress test and attributes the
 * resulting latency to the wrong endpoint.
 *
 * repeatedReports is deliberately absent, exactly as on the NBomber side. At two
 * virtual users that 1ms endpoint generated more traffic than every other
 * scenario combined and held the container at 87-94% CPU, because in a closed
 * model throughput is an output of concurrency and latency rather than something
 * you set. A closed model simply cannot express a traffic mix.
 */
export default defineProfile({
  name: 'load',
  question: 'At expected peak, do we meet our service levels?',

  scenarios: [
    // Warm-up runs first and alone; everything measured starts behind it. Its row
    // in the results exists to be ignored.
    {
      name: 'warmup',
      executor: 'constant-vus',
      vus: 2,
      duration: WARMUP,
    },

    steady('baseline', 2),
    steady('nPlusOne', 3, ['one_query', 'n_queries']),
    steady('pooledQueue', 6),
    steady('lockContention', 2),
  ],

  /**
   * Budgets, not baselines — the same numbers the NBomber suite asserts.
   *
   * These are requirements with real headroom over what was measured, not a tight
   * fit to it. An SLO states what is acceptable; detecting a 10% slowdown is a
   * different job with different tooling, and a gate that fails on noise gets
   * disabled within a fortnight.
   *
   * The pooledQueue p50 budget of 70ms is the load-bearing one: service time is
   * 54ms, so a p50 above 70ms means requests are waiting for a connection rather
   * than using one. That single number separates "slow because the work takes
   * 54ms" from "slow because we are past the knee".
   *
   * Note what k6 makes easy here that NBomber does not: a threshold on a step is
   * just a threshold on that step's metric, with no special API. NBomber needs a
   * separate Threshold.Create overload taking a step name.
   */
  thresholds: {
    'dur__baseline': ['med<=10', 'p(99)<=50'],
    'fails__baseline': ['count==0'],

    'dur__nPlusOne__one_query': ['p(99)<=50'],
    'dur__nPlusOne__n_queries': ['p(99)<=150'],
    'fails__nPlusOne': ['count==0'],

    'dur__pooledQueue': ['med<=70', 'p(99)<=150'],
    'fails__pooledQueue': ['count==0'],

    'dur__lockContention': ['med<=25', 'p(99)<=40'],
    'fails__lockContention': ['count==0'],

    // Throughput floors expressed as counts over the measured window, not as
    // rates.
    //
    // k6 computes a Counter's rate over the whole test duration rather than the
    // duration of the scenario that produced it, so a scenario offset behind a
    // warm-up reports a rate diluted by the time it was not running. Measured
    // here: pooledQueue showed 73.9/s on a 28s test while its own 18s window
    // carried 115.2/s, and a rate>=90 threshold failed a service that was
    // comfortably meeting it.
    //
    // NBomber has no equivalent trap, because it reports per scenario natively.
    // This is the sharpest difference the two suites have surfaced so far.
    'reqs__pooledQueue': [`count>=${Math.floor(90 * MEASURED_SECONDS)}`],
    'reqs__lockContention': [`count>=${Math.floor(100 * MEASURED_SECONDS)}`],
  },
});

function steady(name, vus, steps) {
  return {
    name,
    steps,
    executor: 'constant-vus',
    vus,
    duration: `${Math.round(MEASURED_SECONDS * 1000)}ms`,

    // Offset behind the warm-up, so none of its samples land in these metrics.
    // This is the manual equivalent of NBomber's WithWarmUpDuration.
    startTime: WARMUP,

    // Named so the threshold rate calculations are not skewed by the graceful
    // stop window, which would otherwise count the tail of a scenario against a
    // longer wall clock.
    gracefulStop: dur(5),
  };
}
