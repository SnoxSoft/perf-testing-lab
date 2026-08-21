import { SUMMARY_TREND_STATS } from './report.js';

/**
 * Builds a profile from a single declaration.
 *
 * The reason this exists rather than writing k6 options directly: the metric
 * registry is keyed by scenario name, and so is k6's scenario map. If those two
 * lists ever disagree, a scenario records into a metric that was never
 * registered and the run dies mid-flight — or worse, quietly reports nothing.
 * Deriving both from one array makes the drift impossible instead of merely
 * unlikely.
 *
 * @param {{
 *   name: string,
 *   question: string,
 *   failOnErrors?: boolean,
 *   requiresFreshTarget?: boolean,
 *   thresholds?: object,
 *   scenarios: Array<{name: string, exec?: string, steps?: string[]}>,
 * }} spec
 */
export function defineProfile(spec) {
  const scenarios = {};

  for (const declared of spec.scenarios) {
    const { name, steps, ...config } = declared;

    scenarios[name] = {
      // Default the body to a function of the same name, so a scenario only
      // states `exec` when it deliberately differs.
      exec: config.exec || name,
      ...config,
    };
  }

  return {
    name: spec.name,
    question: spec.question,
    failOnErrors: spec.failOnErrors !== false,
    requiresFreshTarget: spec.requiresFreshTarget === true,

    /** What metrics.register needs: names and their steps. */
    declarations: spec.scenarios.map(({ name, steps }) => ({ name, steps })),

    options: {
      scenarios,
      thresholds: spec.thresholds || {},

      // The schema needs p(75) and p(99), and k6's defaults include neither.
      // Without this they are absent from the summary and the schema silently
      // carries zeroes.
      summaryTrendStats: SUMMARY_TREND_STATS,

      // Discard k6's own per-URL sub-metrics. The endurance profile generates a
      // unique path per request, which would otherwise produce a time series per
      // request.
      noConnectionReuse: false,
      discardResponseBodies: false,
    },
  };
}
