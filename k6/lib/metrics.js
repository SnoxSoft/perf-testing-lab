import { Counter, Trend } from 'k6/metrics';

// Per-scenario metric registry.
//
// k6 aggregates by metric, not by scenario, so the built-in http_req_duration is
// one distribution across everything a run did. NBomber reports per scenario and
// per step natively. To make the two comparable, every scenario and step gets its
// own Trend and Counters here, which is what lets the run schema carry the same
// shape whichever tool produced it.
//
// Two constraints shape this file, and both are easy to get wrong:
//
// 1. k6 metrics must be constructed in the init context, never inside an
//    iteration. So the registry is built up front from the selected profile's
//    declared scenarios rather than created on first use.
//
// 2. handleSummary runs in its own context, separate from every VU. Plain script
//    state accumulated during iterations is *not* visible there — it comes back
//    empty, not merely incomplete. Anything the summary needs has to be a metric.
//    That is why status codes below are counters over a fixed set of codes rather
//    than a tally in an object, which was the first thing tried here and would
//    have silently reported no status codes at all.

const trends = {};
const requests = {};
const failures = {};
const statuses = {};

/**
 * The status outcomes worth counting separately.
 *
 * A fixed set, because a counter cannot be created at init for a code first seen
 * mid-run. These are the ones the target actually produces plus the two
 * client-side outcomes; anything else lands in "other" rather than disappearing.
 *
 * 429 is listed deliberately: it is a refusal, not a failure, and keeping it
 * visible is what stops a spike test reporting a healthy service as broken.
 */
export const STATUS_BUCKETS = ['200', '401', '429', '500', '503', 'timeout', 'transport', 'other'];

function key(scenario, step) {
  return step ? `${scenario}__${step}` : scenario;
}

/**
 * Declares the metrics a profile needs. Called once at init from main.js.
 *
 * @param {Array<{name: string, steps?: string[]}>} scenarios
 */
export function register(scenarios) {
  for (const scenario of scenarios) {
    const names = [key(scenario.name), ...(scenario.steps || []).map((s) => key(scenario.name, s))];

    for (const name of names) {
      if (trends[name]) {
        continue;
      }

      // `true` makes the Trend record a time value, so k6 formats it as a
      // duration and the percentiles come out in milliseconds.
      trends[name] = new Trend(`dur__${name}`, true);
      requests[name] = new Counter(`reqs__${name}`);
      failures[name] = new Counter(`fails__${name}`);
    }

    // Status counters are per scenario only. Per step as well would double-count
    // without adding anything: the interesting question is what mix of outcomes
    // a scenario produced.
    const scenarioKey = key(scenario.name);
    statuses[scenarioKey] = {};

    for (const bucket of STATUS_BUCKETS) {
      statuses[scenarioKey][bucket] = new Counter(`status__${scenarioKey}__${bucket}`);
    }
  }
}

/**
 * Records one request against a scenario, and optionally a step within it.
 *
 * A step observation is recorded twice on purpose — once against the step and
 * once against the scenario — because NBomber reports both, and a comparison
 * missing one of them would not line up.
 */
export function record(scenario, step, durationMs, failed, statusCode) {
  observe(key(scenario), durationMs, failed);

  if (step) {
    observe(key(scenario, step), durationMs, failed);
  }

  const bucket = STATUS_BUCKETS.includes(String(statusCode)) ? String(statusCode) : 'other';
  const counters = statuses[key(scenario)];

  if (counters) {
    counters[bucket].add(1);
  }
}

function observe(name, durationMs, failed) {
  if (!trends[name]) {
    // A scenario that forgot to declare itself would otherwise vanish silently
    // from the results, which is worse than a loud failure.
    throw new Error(`metric '${name}' was not registered; add it to the profile's scenario list`);
  }

  trends[name].add(durationMs);
  requests[name].add(1);

  if (failed) {
    failures[name].add(1);
  }
}
