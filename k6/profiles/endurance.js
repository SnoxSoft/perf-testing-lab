import { defineProfile } from '../lib/profile.js';
import { SCALE, scaledSeconds } from '../lib/config.js';
import { sampling } from '../lib/observer.js';

/**
 * Moderate load held over time. Duration is the variable.
 *
 * The only shape where the load itself is uninteresting: 50 rps is roughly a
 * seventh of the pool endpoint's capacity, and nothing here is near saturation.
 * The question is entirely about time, and it is the one no other shape can
 * answer — what degrades only after hours?
 *
 * Two arms, sequential, differing in one thing:
 *
 *   arm A  uniqueReports    every request retains ~8KB forever
 *   arm B  repeatedReports  identical work, one cache entry
 *
 * They cannot overlap. Heap is a single shared number, so simultaneous arms would
 * make it impossible to attribute growth to either. That constraint is general:
 * memory experiments have to be serialised in a way latency experiments do not.
 *
 * Each arm is split into consecutive windows registered as separate scenarios,
 * which turns the summary into a latency-over-time table. This matters more here
 * than anywhere else — a soak whose result is a single aggregate p99 has thrown
 * away the only dimension it was measuring. The NBomber run found arm A degrading
 * monotonically, +15% on the mean and +27% on p95 across four windows, while arm
 * B stayed flat.
 *
 * Known limitation, the same one the NBomber profile carries: arm B runs second,
 * on a heap already inflated by arm A. It is a valid control for whether further
 * growth occurs, not a like-for-like latency baseline. A true control needs a
 * fresh process per arm, which is why the bench harness restarts the target
 * between repetitions of this profile.
 */
const WINDOWS = 4;
const WINDOW_SECONDS = 120;
const RATE = 50;

const window = scaledSeconds(WINDOW_SECONDS);
const lead = scaledSeconds(10);

// A gap between the arms with the observer still sampling. Whether the heap falls
// back during an idle period is the diagnostic that separates a leak from mere
// caching: a bounded cache under pressure releases, and a leak does not.
const settle = scaledSeconds(30);

const armAStart = lead;
const armBStart = lead + window * WINDOWS + settle;
const total = armBStart + window * WINDOWS + scaledSeconds(20);

function windowed(exec, arm, index, startAt) {
  return {
    name: `${arm}_w${index + 1}`,
    exec,
    executor: 'constant-arrival-rate',
    rate: RATE,
    timeUnit: '1s',
    duration: `${Math.round(window * 1000)}ms`,
    startTime: `${Math.round((startAt + window * index) * 1000)}ms`,
    preAllocatedVUs: 10,
    maxVUs: 50,
    gracefulStop: '10s',
  };
}

export default defineProfile({
  name: 'endurance',
  question: 'What degrades only with time? (Unique keys leak, repeated keys do not.)',
  requiresFreshTarget: true,

  scenarios: [
    sampling(`${Math.round(total * 1000)}ms`),

    ...Array.from({ length: WINDOWS }, (_, i) => windowed('uniqueReports', 'A_unique', i, armAStart)),
    ...Array.from({ length: WINDOWS }, (_, i) => windowed('repeatedReports', 'B_repeated', i, armBStart)),
  ],

  /**
   * The memory budget as a native threshold — something k6 can express and
   * NBomber cannot.
   *
   * Because the observer's samples are ordinary k6 metrics, a ceiling on the
   * heap is just a threshold like any other, and it fails the run through the
   * same mechanism as a latency objective. On the NBomber side this had to live
   * outside the threshold system entirely, as a HeapGrowthBudgetMb property
   * checked by hand after the run, because no expression over ScenarioStats can
   * reach a server-side measurement.
   *
   * The ceiling is absolute rather than a growth figure, since a threshold reads
   * one metric's statistics and cannot subtract the first sample from the peak.
   * Scaled with the run length, because a shorter run leaks proportionally less.
   */
  thresholds: {
    'observed__heap_mb': [`max<=${Math.round(10 + 60 * SCALE)}`],
  },
});
