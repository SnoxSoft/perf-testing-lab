import { defineProfile } from '../lib/profile.js';
import { WARMUP, scaledSeconds } from '../lib/config.js';

/**
 * The cost of getting correlation wrong, measured.
 *
 * Both arms hit the same protected endpoint with the same identities under the
 * same closed-model load. The only difference is how often they authenticate:
 * every iteration, or once per virtual user with a refresh before expiry.
 *
 * The NBomber run measured the cost precisely: 7,179 auth requests against 10,
 * and orders throughput of 159.53/s against 318.11/s. Half of the naive arm's
 * traffic was login. Both arms spent almost the same total request budget and the
 * cached one converted it into twice the useful work.
 *
 * Closed model is required, not preferred. Per-virtual-user token caching lives
 * in module state, which k6 gives each VU its own copy of — the direct equivalent
 * of NBomber's ScenarioInstanceData. Under an arrival-rate executor k6 recycles
 * VUs from a pool, so the token count stops being a clean measure of per-user
 * caching. NBomber has the sharper version of this problem: every arrival is a
 * brand new instance, so the cache is empty every time and the cached arm
 * silently becomes the naive one.
 *
 * Read the step latencies, not the scenario latencies. Both arms include think
 * time outside the steps, so scenario latency carries the pacing while step
 * latency carries the service. An objective written against the scenario total
 * would move whenever somebody adjusted the think time.
 *
 * The headline number is the auth step's request *count*. In the naive arm it
 * equals the orders count; in the cached arm it should be close to the virtual
 * user count for the whole run.
 */
const VIRTUAL_USERS = 10;
const MEASURED_SECONDS = scaledSeconds(45);
const GAP_SECONDS = scaledSeconds(10);

const armAStart = scaledSeconds(10);
const armBStart = armAStart + MEASURED_SECONDS + GAP_SECONDS;

function arm(name, exec, startAt) {
  return {
    name,
    exec,
    steps: ['auth', 'orders'],
    executor: 'constant-vus',
    vus: VIRTUAL_USERS,
    duration: `${Math.round(MEASURED_SECONDS * 1000)}ms`,
    startTime: `${Math.round(startAt * 1000)}ms`,
    gracefulStop: '10s',
  };
}

export default defineProfile({
  name: 'correlation',
  question: 'What does authenticating every iteration cost? (Same work, token per iteration vs per user.)',

  scenarios: [
    // Warm-up first and alone. Token issuance is deliberately expensive, so an
    // unwarmed first request would land a 25ms outlier plus JIT in the measured
    // window.
    {
      name: 'warmup',
      exec: 'authCached',
      steps: ['auth', 'orders'],
      executor: 'constant-vus',
      vus: 2,
      duration: WARMUP,
    },

    // Sequential arms. Token issuance costs real CPU on a two-core container, so
    // running both at once would let the naive arm's login traffic inflate the
    // cached arm's latency.
    arm('auth_naive', 'authNaive', armAStart),
    arm('auth_cached', 'authCached', armBStart),
  ],

  thresholds: {
    'fails__auth_naive': ['count==0'],
    'fails__auth_cached': ['count==0'],

    // The orders endpoint is cheap and should stay cheap in both arms. If this
    // moves, the difference between the arms is not what it appears to be.
    'dur__auth_cached__orders': ['p(99)<=50'],
  },
});
