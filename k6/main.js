import http from 'k6/http';
import exec from 'k6/execution';
import { TARGET } from './lib/config.js';
import { register } from './lib/metrics.js';
import { summarise } from './lib/report.js';
import PROFILES from './profiles/index.js';

// Single entry point. The profile is chosen with --env PROFILE=<name>, mirroring
// `perflab-nbomber <profile>` on the other side.
//
// k6 requires options to be a static export evaluated during init, so the profile
// is resolved here rather than at run time. That is also why every scenario body
// has to be re-exported from this module: k6 resolves a scenario's `exec` against
// the entry module's exports and nowhere else.

const requested = __ENV.PROFILE || 'smoke';
const profile = PROFILES[requested];

if (!profile) {
  throw new Error(
    `unknown profile '${requested}'. Available: ${Object.keys(PROFILES).sort().join(', ')}`);
}

// Metrics must be constructed during init, so the registry is populated from the
// selected profile's declarations before any iteration runs.
register(profile.declarations);

// In catalogue mode the profile is irrelevant: a single no-op iteration is enough
// to reach handleSummary, which is the only place a k6 script can emit anything.
// Without this override, asking the suite to describe itself would run a full
// load test first.
export const options = __ENV.PERFLAB_LIST
  ? { scenarios: { list: { executor: 'per-vu-iterations', vus: 1, iterations: 1, exec: 'noop' } } }
  : profile.options;

/** Does nothing, so catalogue mode costs one iteration and no requests. */
export function noop() {}

/**
 * Machine-readable self-description, for the bench harness.
 *
 * The harness needs to know whether repeated runs require a fresh target
 * *before* it runs anything. Keeping that knowledge in the harness as well would
 * mean two lists and one of them going stale, so the suite answers for itself —
 * the same contract the NBomber runner satisfies with --list.
 */
export function catalog() {
  return {
    tool: 'k6',
    toolVersion: __ENV.K6_VERSION || 'unknown',
    profiles: Object.values(PROFILES).map((p) => ({
      name: p.name,
      question: p.question,
      failOnErrors: p.failOnErrors,
      requiresFreshTarget: p.requiresFreshTarget,
    })),
  };
}

/**
 * Fail fast if the target is not up.
 *
 * Learned from the NBomber side: a run against a stopped target completes
 * perfectly happily and produces a full set of transport errors that look exactly
 * like a catastrophic regression. Aborting is far better than recording a
 * baseline of nothing.
 */
export function setup() {
  if (__ENV.PERFLAB_LIST) {
    return {};
  }

  const response = http.get(`${TARGET}/health`, { timeout: '5s' });

  if (response.status !== 200) {
    exec.test.abort(
      `the target at ${TARGET} is not healthy (status ${response.status}). ` +
      'Try: docker compose up -d');
  }

  return {};
}

export function handleSummary(data) {
  if (__ENV.PERFLAB_LIST) {
    return { stdout: JSON.stringify(catalog(), null, 2) + '\n' };
  }

  const durationSeconds = (data.state && data.state.testRunDurationMs)
    ? data.state.testRunDurationMs / 1000
    : 0;

  return summarise(data, profile, durationSeconds);
}

// Every scenario body, re-exported so k6 can resolve `exec` against this module.
export {
  generatorCeiling,
  baseline,
  nPlusOne,
  pooledQueue,
  lockContention,
  slowDependency,
  slowDependencyHolding,
  uniqueReports,
  repeatedReports,
  allocationPressure,
  rateLimitedSearch,
  authNaive,
  authCached,
  warmup,
} from './lib/scenarios.js';

export { observer } from './lib/observer.js';
