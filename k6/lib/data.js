import exec from 'k6/execution';

// Test identities and the three ways to hand them out, mirroring TestData in the
// NBomber suite.
//
// Worth noting for the tool comparison: NBomber 6 removed its DataFeed API, so
// both suites now do this with plain language constructs indexed off the
// execution context. k6 never had an equivalent abstraction to remove. The three
// patterns below mean genuinely different things and choosing the wrong one is a
// common way to produce confident, wrong numbers.

/**
 * Deliberately more identities than any profile has virtual users. A data set
 * smaller than the VU count silently becomes shared state, and shared identities
 * produce cache hits a real user population would not.
 */
export const USERS = [
  'alice', 'bob', 'carol', 'dave', 'erin', 'frank', 'grace', 'heidi',
  'ivan', 'judy', 'karl', 'linda', 'mallory', 'niaj', 'olivia', 'peggy',
  'quentin', 'rupert', 'sybil', 'trent', 'ursula', 'victor', 'wendy',
  'xavier', 'yvonne', 'zach',
];

/**
 * A different identity every iteration, cycling through the set.
 *
 * Use when each request should look like a distinct user. This is the pattern
 * that stops a load test accidentally measuring one cache entry. Deterministic,
 * so two runs offer the same sequence.
 *
 * iterationInTest is the equivalent of NBomber's ctx.InvocationNumber.
 */
export function circular() {
  return USERS[exec.scenario.iterationInTest % USERS.length];
}

/**
 * One identity per virtual user, stable for the whole run.
 *
 * Required whenever the identity carries session state — a token, a cart, a
 * connection. Cycling identities per iteration would invalidate a cached token on
 * every request and quietly convert a correlation test into a login benchmark.
 *
 * vu.idInTest is the equivalent of NBomber's ScenarioInfo.InstanceNumber.
 */
export function perVirtualUser() {
  return USERS[exec.vu.idInTest % USERS.length];
}

/**
 * Uniformly random: the most realistic and the least reproducible.
 *
 * Two runs draw different sequences, so a latency difference between them can be
 * the data rather than the code. Fine for exploration, disqualifying for a
 * committed baseline — which is why nothing in this suite uses it by default.
 */
export function random() {
  return USERS[Math.floor(Math.random() * USERS.length)];
}

/**
 * A key no previous iteration has used, for the endurance profile's leaking arm.
 *
 * Combining the VU id with the in-test iteration number guarantees uniqueness
 * across VUs without coordination, which is what makes the server retain a fresh
 * entry for every single request.
 */
export function uniqueKey() {
  return `${exec.vu.idInTest}-${exec.scenario.iterationInTest}`;
}
