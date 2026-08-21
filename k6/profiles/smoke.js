import { defineProfile } from '../lib/profile.js';

/**
 * One virtual user, a handful of iterations, every scenario.
 *
 * A smoke test does not test the system — it tests the test. It answers whether
 * every scenario connects, authenticates, parses and asserts correctly, and it
 * answers it in under a minute rather than discovering a typo forty minutes into
 * a capacity ramp. This is the profile that belongs on every pull request.
 *
 * Iterations rather than a duration, so the run takes the same time on a laptop
 * and on a slow CI agent. Duration-based shapes belong only where something is
 * actually being measured.
 *
 * No warm-up, deliberately. Warm-up exists to keep cold-start samples out of the
 * percentiles of a measured run, and this measures nothing — paying for it would
 * only slow down the fastest feedback loop in the suite.
 */
export default defineProfile({
  name: 'smoke',
  question: 'Does every scenario actually work? (Validates the test, not the system.)',

  scenarios: [
    fixed('generatorCeiling', 20),
    fixed('baseline', 10),
    fixed('nPlusOne', 10, ['one_query', 'n_queries']),
    fixed('pooledQueue', 10),
    fixed('lockContention', 10),

    // Three, not ten: this endpoint waits two seconds by design and a smoke test
    // has no reason to spend thirty seconds proving it.
    fixed('slowDependency', 3),
    fixed('slowDependencyHolding', 3),

    fixed('uniqueReports', 10),
    fixed('repeatedReports', 10),
    fixed('allocationPressure', 10),
    fixed('rateLimitedSearch', 10),
    fixed('authCached', 10, ['auth', 'orders']),
  ],

  thresholds: {
    // The only assertion a smoke test should make: nothing was broken enough to
    // fail. Latency budgets belong in the load profile, where the numbers mean
    // something.
    'fails__generatorCeiling': ['count == 0'],
    'fails__baseline': ['count == 0'],
    'fails__nPlusOne': ['count == 0'],
    'fails__pooledQueue': ['count == 0'],
    'fails__lockContention': ['count == 0'],
    'fails__slowDependency': ['count == 0'],
    'fails__slowDependencyHolding': ['count == 0'],
    'fails__uniqueReports': ['count == 0'],
    'fails__repeatedReports': ['count == 0'],
    'fails__allocationPressure': ['count == 0'],
    'fails__rateLimitedSearch': ['count == 0'],
    'fails__authCached': ['count == 0'],
  },
});

function fixed(name, iterations, steps) {
  return {
    name,
    steps,
    executor: 'per-vu-iterations',
    vus: 1,
    iterations,

    // Generous, because slowDependencyHolding at three iterations against a
    // freshly started target can take a while and a smoke test failing on its own
    // timeout would be pure noise.
    maxDuration: '2m',
  };
}
