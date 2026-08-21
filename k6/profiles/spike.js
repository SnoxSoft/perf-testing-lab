import { defineProfile } from '../lib/profile.js';
import { scaledSeconds } from '../lib/config.js';
import { sampling } from '../lib/observer.js';

/**
 * Instant burst, then back to normal. The subject is recovery, not the burst.
 *
 * Everyone tests that a spike causes damage. What matters operationally is
 * whether the service returns to its former latency once the spike passes, and
 * how long that takes — a system that recovers in five seconds and one that stays
 * degraded for ten minutes behave identically during the spike itself.
 *
 * Three plateaus at the same rate either side of a burst, so before and after are
 * directly comparable. That comparison is the entire result.
 *
 * The NBomber run found the damage outlasted the burst by a wide margin: a ten
 * second 3x spike left pooledQueue's p50 at 7323ms against 53.82ms before, still
 * degraded forty seconds later. The arithmetic predicts it — backlog divided by
 * capacity minus ongoing arrivals — and the denominator is the part people
 * forget.
 *
 * The rate limited endpoint is included as the counter-example. It is designed to
 * survive a spike by refusing work, so its latency should barely move while its
 * 429 count climbs. Any gate treating 429 as a failure would report the one
 * endpoint that handled the spike correctly as the one that broke.
 */
const STEADY_SECONDS = 25;
const BURST_SECONDS = 10;
const LEAD_SECONDS = 5;

// Recovery gets longer than the burst that caused it, because a queue takes
// longer to drain than it took to fill.
const RECOVERY_SECONDS = 40;

const steady = scaledSeconds(STEADY_SECONDS);
const burst = scaledSeconds(BURST_SECONDS);
const lead = scaledSeconds(LEAD_SECONDS);
const recovery = scaledSeconds(RECOVERY_SECONDS);

const burstStart = lead + steady;
const recoveryStart = burstStart + burst;
const total = recoveryStart + recovery + scaledSeconds(5);

function plateau({ name, exec, rate, startAt, duration, maxLatencySeconds }) {
  return {
    name,
    exec,
    executor: 'constant-arrival-rate',
    rate,
    timeUnit: '1s',
    duration: `${Math.round(duration * 1000)}ms`,
    startTime: `${Math.round(startAt * 1000)}ms`,
    preAllocatedVUs: Math.max(10, Math.ceil(rate * 0.5)),
    maxVUs: Math.max(50, Math.ceil(rate * maxLatencySeconds)),
    gracefulStop: '30s',
  };
}

export default defineProfile({
  name: 'spike',
  question: 'After a 3x burst, does latency return to what it was?',
  failOnErrors: false,

  scenarios: [
    sampling(`${Math.round(total * 1000)}ms`),

    plateau({ name: 'pool_1_before', exec: 'pooledQueue', rate: 100, startAt: lead, duration: steady, maxLatencySeconds: 2 }),
    plateau({ name: 'pool_2_burst', exec: 'pooledQueue', rate: 900, startAt: burstStart, duration: burst, maxLatencySeconds: 25 }),

    // Same rate as "before". Any difference is the cost of the spike.
    plateau({ name: 'pool_3_after', exec: 'pooledQueue', rate: 100, startAt: recoveryStart, duration: recovery, maxLatencySeconds: 25 }),

    plateau({ name: 'search_1_before', exec: 'rateLimitedSearch', rate: 40, startAt: lead, duration: steady, maxLatencySeconds: 2 }),
    plateau({ name: 'search_2_burst', exec: 'rateLimitedSearch', rate: 900, startAt: burstStart, duration: burst, maxLatencySeconds: 2 }),
    plateau({ name: 'search_3_after', exec: 'rateLimitedSearch', rate: 40, startAt: recoveryStart, duration: recovery, maxLatencySeconds: 2 }),
  ],
});
