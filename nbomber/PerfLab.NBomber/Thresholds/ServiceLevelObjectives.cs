using NBomber.Contracts;
using NBomber.CSharp;

namespace PerfLab.NBomber.Thresholds;

/// <summary>
/// Service level objectives, expressed as NBomber thresholds.
///
/// The reason this file exists is a finding from the capacity ladders: the pool
/// endpoint served 500 req/s at 7.1 seconds mean latency with **zero failed
/// requests**, because the client timeout sits far above the worst case. Any gate
/// built on error rate alone would have called that a perfectly healthy service.
/// Saturation is a latency failure long before it is an availability failure, so
/// a gate without latency thresholds does not gate anything that matters.
///
/// Two conventions here, both deliberate.
///
/// **Literal numbers, not captured variables.** ThresholdResult exposes
/// CheckExpression as a string, which NBomber renders into the report. A closure
/// over a config object renders as "value(Slo).P99LatencyMs" and tells a reader
/// nothing, whereas an inline literal renders as
/// "stats.Ok.Latency.Percent99 &lt;= 150". Losing the DRY abstraction buys a
/// report that explains itself, which is the better trade for something whose
/// whole job is to be read when it fails.
///
/// **Budgets, not baselines.** Every number below is a requirement with real
/// headroom over what was measured, not a tight fit to it. An SLO is a promise
/// about acceptable behaviour; detecting a 10% slowdown is a different job with
/// different tooling, and conflating them produces a gate that fails on noise
/// and gets disabled within a fortnight.
///
/// Measured run-to-run spread across two runs of the load profile was under 3%
/// on every percentile, so these budgets are generous by choice rather than by
/// necessity.
/// </summary>
public static class ServiceLevelObjectives
{
    /// <summary>
    /// Single indexed join. Observed at expected peak: p50 1.94ms, p99 24.11ms.
    ///
    /// The p99 budget looks loose relative to a 1.94ms p50, and that gap is real:
    /// the tail comes from CPU scheduling on a two-core container, not from the
    /// query. Pretending otherwise would produce a threshold that fails whenever
    /// the host is busy.
    /// </summary>
    public static ScenarioProps WithBaselineSlo(this ScenarioProps scenario) =>
        scenario.WithThresholds(
            Threshold.Create(stats => stats.Ok.Latency.Percent50 <= 10),
            Threshold.Create(stats => stats.Ok.Latency.Percent99 <= 50),
            Threshold.Create(stats => stats.Fail.Request.Count == 0));

    /// <summary>
    /// The connection pool queue. Observed: p50 51.97ms, p99 75.01ms, 112.52 rps.
    ///
    /// The p50 budget of 70ms is the important one and it is not arbitrary. Service
    /// time is 54ms, so a p50 above 70ms means requests are waiting for a
    /// connection rather than using one. That single number distinguishes "the
    /// endpoint is slow because the work takes 54ms" from "the endpoint is slow
    /// because we are past the knee", which the capacity ladder put at roughly
    /// 250-300 rps against a 370 rps saturation point.
    ///
    /// The throughput floor starts checking after 10 seconds. Arrival rate needs
    /// time to stabilise, and a throughput assertion evaluated during ramp-up
    /// fails for reasons that have nothing to do with the service.
    /// </summary>
    public static ScenarioProps WithPooledQueueSlo(this ScenarioProps scenario) =>
        scenario.WithThresholds(
            Threshold.Create(stats => stats.Ok.Latency.Percent50 <= 70),
            Threshold.Create(stats => stats.Ok.Latency.Percent99 <= 150),
            Threshold.Create(
                stats => stats.Ok.Request.RPS >= 90,
                startCheckAfter: TimeSpan.FromSeconds(10)),
            Threshold.Create(stats => stats.Fail.Request.Count == 0));

    /// <summary>
    /// The global lock. Observed: p50 16.11ms, p99 17.33ms, 124.65 rps.
    ///
    /// Budgets here are proportionally tighter than elsewhere because the observed
    /// distribution is extremely tight — p50 16.11ms against a p99 of 17.33ms. A
    /// serialised resource degrades violently once utilisation climbs (measured:
    /// 33.6x latency inflation at 81% utilisation, against 1.66x for the pool), so
    /// a loose budget here would allow a genuinely dangerous state to pass.
    /// </summary>
    public static ScenarioProps WithLockContentionSlo(this ScenarioProps scenario) =>
        scenario.WithThresholds(
            Threshold.Create(stats => stats.Ok.Latency.Percent50 <= 25),
            Threshold.Create(stats => stats.Ok.Latency.Percent99 <= 40),
            Threshold.Create(
                stats => stats.Ok.Request.RPS >= 100,
                startCheckAfter: TimeSpan.FromSeconds(10)),
            Threshold.Create(stats => stats.Fail.Request.Count == 0));

    /// <summary>
    /// Per-step thresholds on the N+1 comparison, which is what the step-level
    /// Threshold.Create overload is for.
    ///
    /// Both steps run in the same iteration, so a scenario-level threshold would
    /// average a 2ms query together with a 21ms one and hide the thing being
    /// measured. Asserting on one_query separately is also how a regression in
    /// the *baseline* query stays visible even while the N+1 step dominates the
    /// scenario total.
    ///
    /// Observed: one_query p99 21.12ms, n_queries p99 62.37ms.
    /// </summary>
    public static ScenarioProps WithNPlusOneSlo(this ScenarioProps scenario) =>
        scenario.WithThresholds(
            Threshold.Create("one_query", step => step.Ok.Latency.Percent99 <= 50),
            Threshold.Create("n_queries", step => step.Ok.Latency.Percent99 <= 150),
            Threshold.Create(stats => stats.Fail.Request.Count == 0));

    /// <summary>
    /// The rate limited endpoint, where the interesting assertion is about the
    /// *mix* of outcomes rather than latency.
    ///
    /// 429 is recorded as a successful response by the scenario, so a naive
    /// success-rate threshold passes trivially and proves nothing. What actually
    /// needs guarding is that the limiter is doing its job and the service is not
    /// erroring under refusal: no failures, and latency that stays low because
    /// rejecting a request should be cheap.
    ///
    /// A limiter that started returning 500s instead of 429s, or that began
    /// queueing rather than rejecting, would breach these. A success-rate gate
    /// would notice neither.
    /// </summary>
    public static ScenarioProps WithRateLimitedSlo(this ScenarioProps scenario) =>
        scenario.WithThresholds(
            Threshold.Create(stats => stats.Ok.Latency.Percent99 <= 50),
            Threshold.Create(stats => stats.Fail.Request.Count == 0));

    /// <summary>
    /// The minimum any scenario should satisfy: nothing failed.
    ///
    /// Deliberately separate from the latency objectives above. A shape that is
    /// exploring capacity has no business asserting a latency budget — it is
    /// supposed to end above the knee — but it still has business asserting that
    /// the requests it made were answered. Without this, exploratory profiles
    /// evaluate zero objectives, and a gate reduced to zero checks still exits 0.
    /// </summary>
    public static ScenarioProps WithNoFailures(this ScenarioProps scenario) =>
        scenario.WithThresholds(
            Threshold.Create(stats => stats.Fail.Request.Count == 0));

    /// <summary>
    /// A scenario expected to be comfortably healthy: no failures, and a latency
    /// ceiling supplied by the caller because only the caller knows what the
    /// endpoint is doing.
    ///
    /// Used for the low rungs of a capacity ladder and for a spike's recovery
    /// plateau, where the assertion is not "this is fast" but "this is behaving
    /// as it did before we did anything to it".
    /// </summary>
    public static ScenarioProps WithHealthy(this ScenarioProps scenario, double p50BudgetMs, double p99BudgetMs) =>
        scenario.WithThresholds(
            Threshold.Create(stats => stats.Fail.Request.Count == 0),
            Threshold.Create(stats => stats.Ok.Latency.Percent50 <= p50BudgetMs),
            Threshold.Create(stats => stats.Ok.Latency.Percent99 <= p99BudgetMs));
}
