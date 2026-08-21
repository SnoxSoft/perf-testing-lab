using NBomber.Contracts.Stats;
using PerfLab.NBomber.Profiles;
using PerfLab.NBomber.Scenarios;
using PerfLab.Results;

namespace PerfLab.NBomber;

/// <summary>
/// Translates NBomber's NodeStats into the tool-neutral run result the bench
/// aggregator consumes.
///
/// The mapping is deliberately lossy in one direction and deliberately complete
/// in another: NBomber's report formats carry far more than this, and none of it
/// aggregates across tools, whereas everything here has a k6 equivalent. If a
/// field cannot be produced by both suites it does not belong in the schema.
/// </summary>
public static class RunReporter
{
    public static RunResult Build(
        NodeStats stats,
        IProfile profile,
        string toolVersion,
        int runIndex,
        DateTimeOffset startedAt,
        TimeSpan duration,
        RunOutcome outcome)
    {
        SutObserver.Observation observed = SutObserver.Current;

        return new RunResult
        {
            Tool = "nbomber",
            ToolVersion = toolVersion,
            Profile = profile.Name,
            RunIndex = runIndex,
            StartedAtUtc = startedAt,
            DurationSeconds = Math.Round(duration.TotalSeconds, 2),
            Scale = RunLength.Scale,
            TargetUrl = SutClient.BaseAddress.ToString(),
            Outcome = outcome,
            Scenarios = [.. stats.ScenarioStats.Select(ToScenario)],
            Thresholds =
            [
                .. stats.Thresholds.Select(threshold => new ThresholdOutcome
                {
                    Scenario = threshold.ScenarioName,
                    Step = string.IsNullOrWhiteSpace(threshold.StepName) ? null : threshold.StepName,
                    Expression = threshold.CheckExpression,
                    Failed = threshold.IsFailed,
                }),
            ],

            // Null rather than zeroes when no observer ran, so an aggregator can
            // tell "not measured" from "measured as nothing".
            Observed = observed.Samples > 0
                ? new ObservedResult
                {
                    Samples = observed.Samples,
                    PeakDependencyInFlight = observed.PeakDependencyInFlight,
                    FirstHeapMb = Round(observed.FirstHeapMb),
                    PeakHeapMb = Round(observed.PeakHeapMb),
                    FinalHeapMb = Round(observed.FinalHeapMb),
                    FirstWorkingSetMb = Round(observed.FirstWorkingSetMb),
                    PeakWorkingSetMb = Round(observed.PeakWorkingSetMb),
                    PeakCachedReportEntries = observed.PeakCachedReportEntries,
                    PeakThreadCount = observed.PeakThreadCount,
                    Gen2Collections = observed.Gen2Collections,
                }
                : null,
        };
    }

    private static ScenarioResult ToScenario(ScenarioStats scenario) => new()
    {
        Name = scenario.ScenarioName,
        RequestCount = scenario.Ok.Request.Count + scenario.Fail.Request.Count,
        OkCount = scenario.Ok.Request.Count,
        FailCount = scenario.Fail.Request.Count,
        RequestsPerSecond = Round(scenario.Ok.Request.RPS),
        Latency = ToLatency(scenario.Ok.Latency),
        StatusCodes =
        [
            .. scenario.Ok.StatusCodes
                .Concat(scenario.Fail.StatusCodes)
                .Select(code => new StatusCodeCount { Code = code.StatusCode, Count = code.Count }),
        ],

        // A single-step scenario adds nothing by repeating itself, so the step
        // list is only populated where the split carries information.
        Steps = scenario.StepStats.Length > 1
            ?
            [
                .. scenario.StepStats.Select(step => new StepResult
                {
                    Name = step.StepName,
                    RequestCount = step.Ok.Request.Count + step.Fail.Request.Count,
                    FailCount = step.Fail.Request.Count,
                    RequestsPerSecond = Round(step.Ok.Request.RPS),
                    Latency = ToLatency(step.Ok.Latency),
                }),
            ]
            : [],
    };

    private static LatencySummary ToLatency(LatencyStats latency) => new()
    {
        MinMs = Round(latency.MinMs),
        MeanMs = Round(latency.MeanMs),
        P50Ms = Round(latency.Percent50),
        P75Ms = Round(latency.Percent75),
        P95Ms = Round(latency.Percent95),
        P99Ms = Round(latency.Percent99),
        MaxMs = Round(latency.MaxMs),
        StdDev = Round(latency.StdDev),
    };

    private static double Round(double value) => Math.Round(value, 2);
}
