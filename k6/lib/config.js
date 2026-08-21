// Run configuration, deliberately mirroring the NBomber suite's RunLength and
// SutClient so that a difference between the two suites is a difference in the
// tools rather than in how they were set up.

/**
 * Note the literal 127.0.0.1 rather than localhost.
 *
 * In the NBomber suite this mattered enormously: Windows resolves localhost to
 * ::1 first, Docker publishes on IPv4 only, and .NET waited out the full ~21s
 * platform TCP connect timeout before falling back — one request in five seconds
 * instead of 4575, and it looked like a slow server rather than a client
 * problem.
 *
 * k6 does not suffer this, because Go implements Happy Eyeballs with a ~300ms
 * fallback. That is itself one of the more useful findings in this repository:
 * the same target measured by two tools differed by three orders of magnitude
 * for reasons that had nothing to do with the target. The address is pinned here
 * anyway, so neither suite is measuring its own DNS behaviour.
 */
export const TARGET = __ENV.PERFLAB_SUT_URL || 'http://127.0.0.1:8080';

/**
 * PERFLAB_SCALE multiplies every duration, exactly as in the NBomber suite.
 * Values below 1 trade statistical confidence for speed.
 */
export const SCALE = (() => {
  const parsed = Number.parseFloat(__ENV.PERFLAB_SCALE || '1');
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 1;
})();

/** Where handleSummary writes the shared run schema, when asked to. */
export const RESULTS_PATH = __ENV.PERFLAB_RESULTS || null;

/** 1-based index within a repeat set, supplied by the bench harness. */
export const RUN_INDEX = Number.parseInt(__ENV.PERFLAB_RUN_INDEX || '1', 10);

/**
 * Scaled duration as a k6 duration string.
 *
 * k6 takes durations as strings, so scaling has to happen here rather than being
 * expressed as arithmetic in a profile. Milliseconds keep short scaled runs from
 * collapsing to whole-second granularity.
 */
export function dur(secs) {
  return `${Math.max(1000, Math.round(secs * 1000 * SCALE))}ms`;
}

export function durMinutes(mins) {
  return dur(mins * 60);
}

/**
 * Warm-up is never scaled, for the same reason as in the NBomber suite: JIT,
 * connection pool fill and cache population are fixed costs that do not shrink
 * because the measured window did. A warm-up scaled down to two seconds leaves
 * exactly the cold-start samples it exists to exclude.
 *
 * k6 has no first-class warm-up. NBomber has WithWarmUpDuration, which discards
 * its samples automatically; here the equivalent has to be built by hand as a
 * separate scenario whose metrics are ignored, or by offsetting the measured
 * scenarios with startTime. Both suites pay the cost; only one of them makes it
 * visible in the script.
 */
export const WARMUP_SECONDS = 10;

export const WARMUP = `${WARMUP_SECONDS}s`;

/**
 * Sum of scaled seconds, for computing a startTime offset without repeating the
 * arithmetic in every profile.
 */
export function offset(...secs) {
  const total = secs.reduce((a, b) => a + b, 0);
  return `${Math.round(total * 1000 * SCALE)}ms`;
}
