using PerfLab.Results;

namespace PerfLab.Bench;

/// <summary>
/// The aggregated result of running one profile several times.
///
/// Reports median with the observed range rather than a mean with a standard
/// deviation. Two reasons: at three to five repetitions a standard deviation is
/// not meaningful, and the median is not dragged by the one run where a
/// background process woke up. The range is reported precisely so a reader can
/// see when the runs disagreed enough that the median should not be trusted.
/// </summary>
public sealed record BenchSummary
{
    public int SchemaVersion { get; init; } = 1;

    public required string Tool { get; init; }

    public required string ToolVersion { get; init; }

    public required string Profile { get; init; }

    public int Repetitions { get; init; }

    public double Scale { get; init; }

    public bool FreshTargetPerRun { get; init; }

    public required Provenance Provenance { get; init; }

    /// <summary>
    /// One entry per run, so a suspicious median can be traced back to the run
    /// that caused it.
    /// </summary>
    public IReadOnlyList<string> Outcomes { get; init; } = [];

    public IReadOnlyList<AggregatedScenario> Scenarios { get; init; } = [];

    public AggregatedObservation? Observed { get; init; }
}

public sealed record AggregatedScenario
{
    public required string Name { get; init; }

    public required Stat RequestCount { get; init; }

    public required Stat FailCount { get; init; }

    public required Stat RequestsPerSecond { get; init; }

    public required Stat MeanMs { get; init; }

    public required Stat P50Ms { get; init; }

    public required Stat P95Ms { get; init; }

    public required Stat P99Ms { get; init; }

    public required Stat MinMs { get; init; }

    public IReadOnlyList<AggregatedScenario> Steps { get; init; } = [];
}

public sealed record AggregatedObservation
{
    public required Stat PeakDependencyInFlight { get; init; }

    public required Stat HeapGrowthMb { get; init; }

    public required Stat WorkingSetGrowthMb { get; init; }

    public required Stat PeakCachedReportEntries { get; init; }

    public required Stat Gen2Collections { get; init; }
}

/// <summary>
/// A measurement across repetitions. <see cref="Spread"/> is the range expressed
/// as a fraction of the median, which is the number that says whether the median
/// means anything: a few percent is noise, tens of percent means the runs did not
/// agree and the figure should not be published.
/// </summary>
public sealed record Stat
{
    public double Median { get; init; }

    public double Min { get; init; }

    public double Max { get; init; }

    public double Spread { get; init; }

    public static Stat From(IEnumerable<double> values)
    {
        double[] ordered = [.. values.OrderBy(v => v)];

        if (ordered.Length == 0)
        {
            return new Stat();
        }

        double median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;

        double min = ordered[0];
        double max = ordered[^1];

        return new Stat
        {
            Median = Math.Round(median, 2),
            Min = Math.Round(min, 2),
            Max = Math.Round(max, 2),
            Spread = median == 0 ? 0 : Math.Round((max - min) / median, 4),
        };
    }
}

public static class Aggregator
{
    public static BenchSummary Summarise(
        IReadOnlyList<RunResult> runs,
        ProfileInfo profile,
        Provenance provenance)
    {
        RunResult first = runs[0];

        // Scenario sets are matched by name across runs. A scenario missing from
        // one run would silently change what the median is over, so only names
        // present in every run are aggregated.
        string[] common =
        [
            .. first.Scenarios
                .Select(s => s.Name)
                .Where(name => runs.All(run => run.Scenarios.Any(s => s.Name == name))),
        ];

        return new BenchSummary
        {
            Tool = first.Tool,
            ToolVersion = first.ToolVersion,
            Profile = first.Profile,
            Repetitions = runs.Count,
            Scale = first.Scale,
            FreshTargetPerRun = profile.RequiresFreshTarget,
            Provenance = provenance,
            Outcomes = [.. runs.Select(run => run.Outcome.ToString())],
            Scenarios = [.. common.Select(name => AggregateScenario(runs, name))],
            Observed = runs.All(run => run.Observed is not null)
                ? new AggregatedObservation
                {
                    PeakDependencyInFlight = Stat.From(runs.Select(r => (double)r.Observed!.PeakDependencyInFlight)),
                    HeapGrowthMb = Stat.From(runs.Select(r => r.Observed!.PeakHeapMb - r.Observed.FirstHeapMb)),
                    WorkingSetGrowthMb = Stat.From(runs.Select(r => r.Observed!.PeakWorkingSetMb - r.Observed.FirstWorkingSetMb)),
                    PeakCachedReportEntries = Stat.From(runs.Select(r => (double)r.Observed!.PeakCachedReportEntries)),
                    Gen2Collections = Stat.From(runs.Select(r => (double)r.Observed!.Gen2Collections)),
                }
                : null,
        };
    }

    private static AggregatedScenario AggregateScenario(IReadOnlyList<RunResult> runs, string name)
    {
        ScenarioResult[] instances = [.. runs.Select(run => run.Scenarios.First(s => s.Name == name))];

        string[] stepNames =
        [
            .. instances[0].Steps
                .Select(s => s.Name)
                .Where(step => instances.All(i => i.Steps.Any(s => s.Name == step))),
        ];

        return new AggregatedScenario
        {
            Name = name,
            RequestCount = Stat.From(instances.Select(i => (double)i.RequestCount)),
            FailCount = Stat.From(instances.Select(i => (double)i.FailCount)),
            RequestsPerSecond = Stat.From(instances.Select(i => i.RequestsPerSecond)),
            MeanMs = Stat.From(instances.Select(i => i.Latency.MeanMs)),
            P50Ms = Stat.From(instances.Select(i => i.Latency.P50Ms)),
            P95Ms = Stat.From(instances.Select(i => i.Latency.P95Ms)),
            P99Ms = Stat.From(instances.Select(i => i.Latency.P99Ms)),
            MinMs = Stat.From(instances.Select(i => i.Latency.MinMs)),
            Steps =
            [
                .. stepNames.Select(step =>
                {
                    StepResult[] steps = [.. instances.Select(i => i.Steps.First(s => s.Name == step))];

                    return new AggregatedScenario
                    {
                        Name = step,
                        RequestCount = Stat.From(steps.Select(s => (double)s.RequestCount)),
                        FailCount = Stat.From(steps.Select(s => (double)s.FailCount)),
                        RequestsPerSecond = Stat.From(steps.Select(s => s.RequestsPerSecond)),
                        MeanMs = Stat.From(steps.Select(s => s.Latency.MeanMs)),
                        P50Ms = Stat.From(steps.Select(s => s.Latency.P50Ms)),
                        P95Ms = Stat.From(steps.Select(s => s.Latency.P95Ms)),
                        P99Ms = Stat.From(steps.Select(s => s.Latency.P99Ms)),
                        MinMs = Stat.From(steps.Select(s => s.Latency.MinMs)),
                    };
                }),
            ],
        };
    }
}
