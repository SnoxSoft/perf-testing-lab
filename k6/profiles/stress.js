import { defineProfile } from '../lib/profile.js';
import { scaledSeconds } from '../lib/config.js';
import { sampling } from '../lib/observer.js';

/**
 * Past capacity on purpose, to characterise *how* the service fails.
 *
 * A controlled experiment with two arms hitting the same two second downstream
 * dependency at the same rates. The single variable is whether a pooled
 * connection is held across the wait.
 *
 *   arm A  slowDependency         touches no pool
 *   arm B  slowDependencyHolding  holds one connection for the whole call
 *
 * The NBomber run established the result: arm A stayed flat at 2003ms with zero
 * failures through 200 rps, because a pending timer is not a resource and
 * hundreds of concurrent in-flight calls are close to free on an async stack.
 * Arm B collapsed to exactly 10 rps — pool size divided by dependency latency,
 * 20 / 2s — and failed the rest with 500s after the full 30 second pool
 * acquisition timeout.
 *
 * The conclusion is worth more than either arm: in-flight count does not decide
 * whether a slow dependency is survivable. Whether that in-flight work holds
 * something scarce does.
 *
 * Arms run sequentially. They share the same pool, so running them together
 * would make each one's result partly caused by the other.
 */
const STEP_SECONDS = 20;
const LEAD_SECONDS = 5;
const GAP_SECONDS = 20;
// Stops at 100. Arm B needs virtual users proportional to rate x the 30 second
// pool timeout, so 200 rps would need ~6,400 concurrent VUs — enough to saturate
// the host and starve the very generator doing the measuring. The ceiling is
// already unambiguous at 100.
const RATES = [25, 50, 100];

const step = scaledSeconds(STEP_SECONDS);
const lead = scaledSeconds(LEAD_SECONDS);
const gap = scaledSeconds(GAP_SECONDS);

const armAStart = lead;
const armBStart = lead + step * RATES.length + gap;
const total = armBStart + step * RATES.length + scaledSeconds(30);

/**
 * Arm B needs vastly more virtual users than arm A for the same offered rate.
 *
 * By Little's Law the requirement is rate x latency, and arm B's latency is the
 * 30 second pool timeout rather than the 2 second dependency — so sustaining 200
 * rps against it needs on the order of 6,000 concurrent VUs. That asymmetry is
 * not an inconvenience to be tuned away; it is the same finding from the
 * generator's side. Overloading a resource-starved endpoint is expensive to
 * generate precisely because each request occupies the generator for as long as
 * it occupies the target.
 */
function rung(exec, rate, index, startAt, maxLatencySeconds, arm) {
  return {
    name: `${arm}_${String(index + 1).padStart(2, '0')}_at_${rate}rps`,
    exec,
    executor: 'constant-arrival-rate',
    rate,
    timeUnit: '1s',
    duration: `${Math.round(step * 1000)}ms`,
    startTime: `${Math.round((startAt + step * index) * 1000)}ms`,
    preAllocatedVUs: Math.max(10, Math.ceil(rate * 2)),
    maxVUs: Math.max(50, Math.ceil(rate * maxLatencySeconds)),
    gracefulStop: '35s',
  };
}

export default defineProfile({
  name: 'stress',
  question: 'Past capacity, how does it fail? (Same dependency, with and without holding a connection.)',
  failOnErrors: false,

  scenarios: [
    // The observer must already be sampling before the load starts, or the most
    // interesting moments go unrecorded.
    sampling(`${Math.round(total * 1000)}ms`),

    ...RATES.map((rate, i) => rung('slowDependency', rate, i, armAStart, 4, 'A_free')),
    ...RATES.map((rate, i) => rung('slowDependencyHolding', rate, i, armBStart, 32, 'B_holding')),
  ],
});
