import { RESULTS_PATH, RUN_INDEX, SCALE, TARGET } from './config.js';
import { STATUS_BUCKETS } from './metrics.js';

// Translates k6's summary into the tool-neutral run schema defined in
// tools/PerfLab.Results, so a k6 run and an NBomber run aggregate identically.
//
// The percentiles the schema needs are not k6's defaults, so every profile sets
// summaryTrendStats. Without that, p(75) and p(99) are simply absent from the
// summary and the schema would carry zeroes.
export const SUMMARY_TREND_STATS = ['min', 'avg', 'med', 'p(75)', 'p(95)', 'p(99)', 'max', 'count'];

const SCHEMA_VERSION = 1;

/**
 * @param {object} data k6's summary payload
 * @param {{name: string, declarations: Array<{name: string, steps?: string[]}>}} profile
 * @param {number} durationSeconds
 */
export function buildRunResult(data, profile, durationSeconds) {
  const scenarios = profile.declarations.map((scenario) => toScenario(data, scenario));

  return {
    schemaVersion: SCHEMA_VERSION,
    tool: 'k6',
    toolVersion: (exec_version() || 'unknown'),
    profile: profile.name,
    runIndex: RUN_INDEX,
    startedAtUtc: new Date(Date.now() - durationSeconds * 1000).toISOString(),
    durationSeconds: round(durationSeconds),
    scale: SCALE,
    targetUrl: TARGET,
    outcome: outcomeOf(data, scenarios),
    scenarios,
    thresholds: thresholdOutcomes(data),
    observed: observedFrom(data),
  };
}

function toScenario(data, scenario) {
  const steps = (scenario.steps || []).map((step) =>
    toMeasurement(data, `${scenario.name}__${step}`, step));

  return {
    ...toMeasurement(data, scenario.name, scenario.name),
    statusCodes: statusCodesFor(data, scenario.name),
    steps,
  };
}

function toMeasurement(data, metricKey, name) {
  const trend = data.metrics[`dur__${metricKey}`];
  const requests = data.metrics[`reqs__${metricKey}`];
  const failures = data.metrics[`fails__${metricKey}`];

  const values = (trend && trend.values) || {};
  const requestCount = countOf(requests);
  const failCount = countOf(failures);

  return {
    name,
    requestCount,
    okCount: requestCount - failCount,
    failCount,
    requestsPerSecond: round((requests && requests.values && requests.values.rate) || 0),
    latency: {
      minMs: round(values.min),
      meanMs: round(values.avg),
      p50Ms: round(values.med),
      p75Ms: round(values['p(75)']),
      p95Ms: round(values['p(95)']),
      p99Ms: round(values['p(99)']),
      maxMs: round(values.max),

      // k6's summary does not expose a standard deviation for trends, so the
      // schema carries zero here where NBomber carries a real figure. Recorded as
      // a known asymmetry rather than papered over: any comparison of spread
      // between the suites has to come from repeated runs, which is what the
      // bench harness is for.
      stdDev: 0,
    },
  };
}

function statusCodesFor(data, scenarioName) {
  return STATUS_BUCKETS
    .map((bucket) => ({
      code: bucket,
      count: countOf(data.metrics[`status__${scenarioName}__${bucket}`]),
    }))
    .filter((entry) => entry.count > 0);
}

function thresholdOutcomes(data) {
  const outcomes = [];

  for (const [metric, values] of Object.entries(data.metrics)) {
    if (!values.thresholds) {
      continue;
    }

    for (const [expression, result] of Object.entries(values.thresholds)) {
      outcomes.push({
        // k6 attaches thresholds to metrics, not scenarios, so the metric name is
        // the closest thing to a scope. The naming convention in metrics.js is
        // what makes it recoverable at all.
        scenario: scopeOf(metric),
        step: stepOf(metric),
        expression: `${metric}: ${expression}`,
        failed: result.ok === false,
      });
    }
  }

  return outcomes;
}

function scopeOf(metric) {
  const withoutPrefix = metric.replace(/^(dur|reqs|fails)__/, '');
  return withoutPrefix.split('__')[0];
}

function stepOf(metric) {
  const withoutPrefix = metric.replace(/^(dur|reqs|fails)__/, '');
  const parts = withoutPrefix.split('__');
  return parts.length > 1 ? parts[1] : null;
}

function observedFrom(data) {
  const heap = data.metrics['observed__heap_mb'];

  if (!heap || !heap.values || !heap.values.count) {
    // Null rather than zeroes, so an aggregator can tell "not measured" from
    // "measured as nothing".
    return null;
  }

  const value = (name, stat) => {
    const metric = data.metrics[`observed__${name}`];
    return metric && metric.values ? round(metric.values[stat]) : 0;
  };

  return {
    samples: heap.values.count,
    peakDependencyInFlight: value('dependency_in_flight', 'max'),

    // min stands in for "first". For a monotonically growing leak these coincide;
    // for a flat control arm the distinction does not matter. It is an
    // approximation, and the reason is that k6's summary reports a distribution
    // rather than a series, so there is no "first observation" to read.
    firstHeapMb: value('heap_mb', 'min'),
    peakHeapMb: value('heap_mb', 'max'),
    finalHeapMb: value('heap_mb', 'max'),
    firstWorkingSetMb: value('working_set_mb', 'min'),
    peakWorkingSetMb: value('working_set_mb', 'max'),
    peakCachedReportEntries: value('cached_entries', 'max'),
    peakThreadCount: value('threads', 'max'),
    gen2Collections: value('gen2', 'max'),
  };
}

function outcomeOf(data, scenarios) {
  const breached = Object.values(data.metrics)
    .some((metric) => metric.thresholds
      && Object.values(metric.thresholds).some((result) => result.ok === false));

  if (breached) {
    return 'ThresholdBreached';
  }

  return scenarios.some((scenario) => scenario.failCount > 0)
    ? 'FailuresRecorded'
    : 'Passed';
}

function countOf(metric) {
  return metric && metric.values ? Math.round(metric.values.count || 0) : 0;
}

function round(value) {
  return typeof value === 'number' && Number.isFinite(value) ? Math.round(value * 100) / 100 : 0;
}

function exec_version() {
  // k6 does not expose its own version to a script, so the bench harness passes
  // it in as K6_VERSION. A direct run reports "unknown", which is honest: a
  // baseline should come from the harness anyway.
  return __ENV.K6_VERSION || null;
}

/**
 * A compact console summary plus the schema file.
 *
 * Defining handleSummary replaces k6's built-in report entirely, so the console
 * output has to be rebuilt here. It is written to resemble the NBomber runner's
 * output, because reading two differently shaped summaries side by side is most
 * of the work in comparing the tools.
 */
export function summarise(data, profile, durationSeconds) {
  const result = buildRunResult(data, profile, durationSeconds);
  const lines = [];

  lines.push('');
  lines.push(`profile:  ${profile.name}`);
  lines.push(`question: ${profile.question}`);
  lines.push(`target:   ${TARGET}`);

  if (SCALE !== 1) {
    lines.push(`scale:    ${SCALE}x — fewer samples, noisier percentiles, not a baseline`);
  }

  lines.push('');
  lines.push('scenario                          reqs     rps      mean      p50      p95      p99   fails');

  for (const scenario of result.scenarios) {
    lines.push(row(scenario, false));

    for (const step of scenario.steps) {
      lines.push(row(step, true));
    }
  }

  const breached = result.thresholds.filter((t) => t.failed);

  lines.push('');
  lines.push(`service level objectives: ${result.thresholds.length - breached.length} passed, ` +
    `${breached.length} breached`);

  for (const threshold of breached) {
    lines.push(`  BREACH ${threshold.expression}`);
  }

  if (result.observed) {
    lines.push('');
    lines.push('observed on the server:');
    lines.push(`  heap ${result.observed.firstHeapMb} -> ${result.observed.peakHeapMb} MB ` +
      `(growth ${round(result.observed.peakHeapMb - result.observed.firstHeapMb)} MB)`);
    lines.push(`  working set ${result.observed.firstWorkingSetMb} -> ` +
      `${result.observed.peakWorkingSetMb} MB`);
    lines.push(`  cached report entries ${result.observed.peakCachedReportEntries}`);
  }

  lines.push('');

  const output = { stdout: lines.join('\n') + '\n' };

  if (RESULTS_PATH) {
    output[RESULTS_PATH] = JSON.stringify(result, null, 2);
    output.stdout += `run result: ${RESULTS_PATH}\n`;
  }

  return output;
}

function row(measurement, indented) {
  const name = (indented ? '  ↳ ' : '') + measurement.name;
  const l = measurement.latency;

  return [
    name.padEnd(30),
    String(measurement.requestCount).padStart(8),
    measurement.requestsPerSecond.toFixed(1).padStart(8),
    l.meanMs.toFixed(2).padStart(9),
    l.p50Ms.toFixed(2).padStart(8),
    l.p95Ms.toFixed(2).padStart(8),
    l.p99Ms.toFixed(2).padStart(8),
    String(measurement.failCount).padStart(7),
  ].join('');
}
