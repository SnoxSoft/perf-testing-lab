import { get, post } from './http.js';
import { perVirtualUser, uniqueKey } from './data.js';

// One exported function per pathology, mirroring SutScenarios in the NBomber
// suite name for name. A profile references these by k6's `exec` property, which
// is the closest equivalent to registering a ScenarioProps: the request pattern
// is defined once and the load shape is chosen separately.
//
// Each of these is a k6 scenario body, so it must be exported from main.js for
// the runtime to find it.

/** Framework overhead floor. Establishes what the harness itself can measure. */
export function generatorCeiling() {
  get('/api/echo');
}

/** The reference point: one indexed join. */
export function baseline() {
  get('/api/catalog/products');
}

/**
 * N+1 against the baseline, as two steps in one iteration so their latencies are
 * reported separately and the difference is attributable rather than inferred.
 */
export function nPlusOne() {
  get('/api/catalog/products', { step: 'one_query' });
  get('/api/catalog/products/n-plus-one', { step: 'n_queries' });
}

/** The connection pool queue. Ceiling is pool size / hold time. */
export function pooledQueue() {
  get('/api/queue/pooled');
}

/** Global lock: a single-server queue rather than the pool's twenty. */
export function lockContention() {
  post('/api/queue/reserve');
}

/**
 * Untimed downstream call that holds nothing scarce. The control arm: unbounded
 * async in-flight work turns out to be close to free.
 */
export function slowDependency() {
  get('/api/queue/enrich?subject=perflab');
}

/**
 * The same call while holding a pooled connection. The arm that collapses,
 * because the ceiling becomes pool size divided by dependency latency.
 */
export function slowDependencyHolding() {
  get('/api/queue/enrich-holding?subject=perflab');
}

/**
 * The endurance target. Every iteration asks for a key the server has never
 * seen, so it retains a fresh entry for each one.
 */
export function uniqueReports() {
  get(`/api/reports/${uniqueKey()}`);
}

/** The control for the endurance comparison: identical shape, one key. */
export function repeatedReports() {
  get('/api/reports/always-the-same-key');
}

/** Large object heap pressure. */
export function allocationPressure() {
  get('/api/reports/export');
}

/**
 * The rate limited endpoint, where 429 counts as a successful outcome.
 *
 * The most important measurement judgement in either suite. A 429 means the
 * service refused work it could not safely accept — correct behaviour, and
 * categorically different from a 500 or a timeout. Treating it as a failure
 * would make every spike test report a healthy service as broken.
 */
export function rateLimitedSearch() {
  get('/api/search?q=perflab', { okStatuses: [200, 429] });
}

/** Authenticates on every iteration: the wrong way, measured. */
export function authNaive() {
  const user = perVirtualUser();
  const issued = post(`/api/auth/token?user=${user}`, { step: 'auth' });

  if (!issued.ok) {
    return;
  }

  ordersFor(issued.json('token'), user);
}

/**
 * Authenticates once per virtual user and refreshes before expiry.
 *
 * The cache is a module-scoped variable, which in k6 is per virtual user: each VU
 * gets its own instance of the module. That is exactly the right lifetime for a
 * session token, and it is the direct equivalent of NBomber's
 * ScenarioInstanceData.
 *
 * The same caveat applies to both suites: this only works under a closed model.
 * With an arrival-rate executor k6 recycles VUs from a pool, so a token may be
 * reused across logically different arrivals — which is fine here, but it means
 * the *count* of token requests stops being a clean measure of per-user caching.
 */
let cachedToken = null;

export function authCached() {
  const user = perVirtualUser();
  const now = Date.now();

  if (!cachedToken || cachedToken.user !== user || cachedToken.refreshAt <= now) {
    const issued = post(`/api/auth/token?user=${user}`, { step: 'auth' });

    if (!issued.ok) {
      return;
    }

    // Refreshed ten seconds ahead of expiry rather than at it. A token that is
    // valid when checked can expire in flight and come back 401 — a failure that
    // appears intermittently at low rates and looks like a service fault rather
    // than a test bug.
    cachedToken = {
      user,
      token: issued.json('token'),
      refreshAt: now + (issued.json('expiresInSeconds') - 10) * 1000,
    };
  }

  ordersFor(cachedToken.token, user);
}

function ordersFor(token, expectedUser) {
  const response = get('/api/orders/mine', {
    step: 'orders',
    headers: { Authorization: `Bearer ${token}` },
  });

  // Assert the response belongs to the identity that asked for it. Without this a
  // test where every virtual user shares one token passes happily, and the cache
  // hit ratio it reports is fiction.
  if (response.ok && response.json('user') !== expectedUser) {
    throw new Error(`identity mismatch: expected ${expectedUser}, got ${response.json('user')}`);
  }
}
