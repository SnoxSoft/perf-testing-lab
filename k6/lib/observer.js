import { Trend } from 'k6/metrics';
import { get } from './http.js';

// Samples the target's own diagnostics as a scenario, mirroring SutObserver in
// the NBomber suite.
//
// Client-side latency describes the symptom. It cannot distinguish a service that
// is slow because work is queued from one that is slow because the heap is
// growing or because calls to a downstream are accumulating. Those are different
// faults with different fixes and they produce identical latency curves.
//
// Trends rather than Gauges, because k6's summary reports a Gauge as its last
// value only. A stress or endurance run ends after the load stops, so the last
// sample is taken while the system is already recovering — the interesting moment
// is the worst one, and only a Trend carries max.
//
// The consequence is that "first" is not directly available: a Trend is a
// distribution, not a series. The report uses min as a stand-in, which coincides
// with first for a monotonically growing leak and is documented as an
// approximation rather than hidden.

const dependencyInFlight = new Trend('observed__dependency_in_flight');
const heapMb = new Trend('observed__heap_mb');
const workingSetMb = new Trend('observed__working_set_mb');
const cachedEntries = new Trend('observed__cached_entries');
const threads = new Trend('observed__threads');
const gen2 = new Trend('observed__gen2');

const BYTES_PER_MB = 1024 * 1024;

export function observer() {
  const response = get('/diagnostics/memory');

  if (!response.ok) {
    return;
  }

  const snapshot = response.json();

  dependencyInFlight.add(snapshot.dependencyCallsInFlight);
  heapMb.add(snapshot.heapBytes / BYTES_PER_MB);
  workingSetMb.add(snapshot.workingSetBytes / BYTES_PER_MB);
  cachedEntries.add(snapshot.cachedReportEntries);
  threads.add(snapshot.threadCount);
  gen2.add(snapshot.gen2Collections);
}

/**
 * One sample per second for the given duration.
 *
 * An arrival-rate executor rather than a fixed VU, so a slow diagnostics response
 * delays that sample rather than the whole series — sampling should not drift
 * just because the target is struggling, which is exactly when the samples matter
 * most.
 *
 * No startTime: the observer must already be running when the load begins, or the
 * most interesting moments go unrecorded.
 */
export function sampling(duration) {
  return {
    name: 'observer',
    executor: 'constant-arrival-rate',
    rate: 1,
    timeUnit: '1s',
    duration,
    preAllocatedVUs: 2,
    maxVUs: 4,
  };
}
