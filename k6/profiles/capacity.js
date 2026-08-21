import { defineProfile } from '../lib/profile.js';
import { scaledSeconds } from '../lib/config.js';

// Stepped arrival-rate ladders, the mirror of the NBomber CapacityProfile.
//
// Open workload model: constant-arrival-rate holds a fixed rate regardless of
// whether the service keeps up, so past capacity the backlog grows and latency
// climbs without bound. A closed model physically cannot show this, because
// fixed workers send less exactly when the service slows down.
//
// Discrete plateaus rather than a continuous ramping-arrival-rate, for the same
// reason as on the NBomber side: a continuous ramp smears every rate together and
// a p99 from the run cannot be attributed to any particular one. Each rung is a
// separate scenario offset by startTime, so the summary becomes the capacity
// table directly.

const STEP_SECONDS = 20;
const WARMUP_SECONDS = 15;

/**
 * The thing k6 makes you confront that NBomber does not.
 *
 * An arrival-rate executor needs its virtual users declared up front. By Little's
 * Law the requirement is rate x latency, and past capacity latency grows without
 * bound — so the VU count needed to *generate* deep overload explodes just when
 * the service is least able to answer. At 500 rps against a pool that is taking
 * seven seconds to respond, sustaining the rate needs thousands of concurrent
 * virtual users.
 *
 * NBomber's Simulation.Inject allocates as needed and never mentions this, which
 * is more convenient and considerably less honest: the same limit exists there,
 * and the NBomber lock ladder ran into it as generator saturation producing
 * self-contradictory numbers. k6 at least warns when it cannot keep up.
 *
 * maxLatencySeconds is therefore a property of each ladder, set from what the
 * endpoint was observed to do at its worst rung.
 */
function ladder({ name, question, exec, rates, warmUpRate, maxLatencySeconds, steps }) {
  const step = scaledSeconds(STEP_SECONDS);
  const warm = scaledSeconds(WARMUP_SECONDS);

  const rung = (rate, index) => ({
    name: `${exec}_${String(index + 1).padStart(2, '0')}_at_${rate}rps`,
    exec,
    steps,
    executor: 'constant-arrival-rate',
    rate,
    timeUnit: '1s',
    duration: `${Math.round(step * 1000)}ms`,
    startTime: `${Math.round((warm + step * index) * 1000)}ms`,
    preAllocatedVUs: Math.max(10, Math.ceil(rate * Math.min(maxLatencySeconds, 1))),
    maxVUs: Math.max(50, Math.ceil(rate * maxLatencySeconds)),
    gracefulStop: '30s',
  });

  return defineProfile({
    name,
    question,

    // A capacity ramp is meant to end above the knee, so timeouts there are the
    // result rather than a broken test.
    failOnErrors: false,

    scenarios: [
      // Explicit warm-up rung. k6 has no built-in warm-up, and on a staggered
      // ladder NBomber's per-scenario warm-up would have been wrong anyway: it
      // runs at t=0 for every scenario in parallel, so later rungs would pollute
      // the first one being measured.
      {
        name: `${exec}_00_warmup`,
        exec,
        steps,
        executor: 'constant-arrival-rate',
        rate: warmUpRate,
        timeUnit: '1s',
        duration: `${Math.round(warm * 1000)}ms`,
        preAllocatedVUs: Math.max(10, warmUpRate),
        maxVUs: Math.max(50, warmUpRate * 4),
      },

      ...rates.map(rung),
    ],
  });
}

/**
 * The connection pool queue: the best teaching case in the suite, because its
 * ceiling is known before the test runs.
 *
 *   ceiling = pool size / hold time = 20 / 0.05s = 400 req/s
 *
 * A capacity test whose answer can be derived first is how the method gets
 * validated. NBomber measured the latency knee at roughly 250-300 rps, well below
 * that saturation point, because queue waiting grows as rho/(1-rho) and blows up
 * long before utilisation reaches one.
 */
export const capacityPool = ladder({
  name: 'capacity-pool',
  question: 'Where is the knee for pooledQueue? Predicted ceiling: 400 rps (20 connections / 50ms hold)',
  exec: 'pooledQueue',
  rates: [100, 200, 300, 350, 400, 450, 500],
  warmUpRate: 50,
  maxLatencySeconds: 11,
});

/**
 * The global lock: a single-server queue rather than the pool's twenty. Nominal
 * ceiling is 1 / 0.005s = 200 rps, but observed service time is nearer 6.5ms so
 * the real ceiling is around 154.
 *
 * The ladder stops at 175 deliberately. Higher rungs pushed the endpoint into
 * collapse on the NBomber side and the measurements stopped meaning anything —
 * reported "ok" latencies exceeded the client timeout, which cannot be true of a
 * request that did not time out. A ladder should bracket the ceiling, not vanish
 * over it.
 */
export const capacityLock = ladder({
  name: 'capacity-lock',
  question: 'Where is the knee for lockContention? Predicted ceiling: ~154 rps (serialised critical section)',
  exec: 'lockContention',
  rates: [25, 50, 75, 100, 125, 150, 175],
  warmUpRate: 15,
  maxLatencySeconds: 14,
});

/**
 * The measurement ceiling for the whole lab: generator plus framework, with no
 * database, no locks and no allocation.
 *
 * Not a test of the service. It is the number every other result has to be read
 * against, because any endpoint measured within an order of magnitude of this
 * figure is partly reporting the harness. NBomber reached 10,000 rps at 6.65ms
 * here, with strain from 12,000 and non-monotonic results by 14,000.
 */
export const ceiling = ladder({
  name: 'ceiling',
  question: 'Where is the knee for echo? NBomber reached ~10,000 rps at 6.65ms',
  exec: 'generatorCeiling',
  // Stops at 8,000. Measured collapse begins above that on this hardware —
  // 10,000 offered delivered 1,457 with 13,041 failures — so higher rungs
  // measure the harness, not the service, and they saturate the host badly
  // enough to make the machine unusable while they run.
  rates: [1000, 2000, 3000, 4000, 6000, 8000],
  warmUpRate: 500,

  // A trivial endpoint, so the VU requirement is small even at high rates.
  maxLatencySeconds: 0.5,
});
