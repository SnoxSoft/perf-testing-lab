using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfLab.Results;

/// <summary>
/// The canonical shape of one measured run, written by whichever tool produced
/// it and read by the bench aggregator.
///
/// Defining our own schema rather than parsing each tool's native report is what
/// makes the two suites comparable at all. NBomber emits HTML, Markdown, CSV and
/// text; k6 emits its own JSON. Aggregating across tools by parsing either would
/// couple the harness to formats that exist to be read by humans and are free to
/// change. Instead each tool writes this, and the aggregator only ever reads
/// this.
///
/// It also fixes the honesty problem that prompted the whole exercise: a run
/// that does not record its own scale factor, target configuration and code
/// version produces numbers nobody can reproduce or even interpret later.
/// </summary>
public sealed record RunResult
{
    /// <summary>
    /// Bumped when a field changes meaning. An aggregator that silently averages
    /// two incompatible schemas is worse than one that refuses.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    public required string Tool { get; init; }

    public required string ToolVersion { get; init; }

    public required string Profile { get; init; }

    /// <summary>1-based index within a repeat set; 1 for a standalone run.</summary>
    public int RunIndex { get; init; } = 1;

    public required DateTimeOffset StartedAtUtc { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary>
    /// The duration multiplier in force. Without this, a table of latencies from
    /// a 0.4-scale run is indistinguishable from a full-length one — which is
    /// exactly the mistake this schema exists to prevent.
    /// </summary>
    public double Scale { get; init; } = 1.0;

    public required string TargetUrl { get; init; }

    public RunOutcome Outcome { get; init; }

    public IReadOnlyList<ScenarioResult> Scenarios { get; init; } = [];

    public IReadOnlyList<ThresholdOutcome> Thresholds { get; init; } = [];

    /// <summary>Server-side observations, when the profile ran an observer.</summary>
    public ObservedResult? Observed { get; init; }
}

/// <summary>
/// Why the run ended as it did. A bare exit code loses the distinction between
/// "the service was too slow" and "requests failed", which are different
/// findings with different fixes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunOutcome>))]
public enum RunOutcome
{
    Passed,
    ThresholdBreached,
    HeapBudgetBreached,
    FailuresRecorded,
}

public sealed record ScenarioResult
{
    public required string Name { get; init; }

    public int RequestCount { get; init; }

    public int OkCount { get; init; }

    public int FailCount { get; init; }

    public double RequestsPerSecond { get; init; }

    public required LatencySummary Latency { get; init; }

    public IReadOnlyList<StatusCodeCount> StatusCodes { get; init; } = [];

    /// <summary>
    /// Per-step figures where the scenario has more than one step. Kept separate
    /// from the scenario total because a scenario-level aggregate averages a 2ms
    /// query together with a 21ms one and hides what is being measured.
    /// </summary>
    public IReadOnlyList<StepResult> Steps { get; init; } = [];
}

public sealed record StepResult
{
    public required string Name { get; init; }

    public int RequestCount { get; init; }

    public int FailCount { get; init; }

    public double RequestsPerSecond { get; init; }

    public required LatencySummary Latency { get; init; }
}

/// <summary>
/// Mean is carried alongside the percentiles deliberately: capacity arithmetic
/// needs the mean, service level objectives need the percentiles, and using one
/// where the other belongs produced a 35% error the first time it was tried.
/// </summary>
public sealed record LatencySummary
{
    public double MinMs { get; init; }

    public double MeanMs { get; init; }

    public double P50Ms { get; init; }

    public double P75Ms { get; init; }

    public double P95Ms { get; init; }

    public double P99Ms { get; init; }

    public double MaxMs { get; init; }

    public double StdDev { get; init; }
}

public sealed record StatusCodeCount
{
    public required string Code { get; init; }

    public int Count { get; init; }
}

public sealed record ThresholdOutcome
{
    public required string Scenario { get; init; }

    public string? Step { get; init; }

    /// <summary>The rendered predicate, so a breach explains itself.</summary>
    public required string Expression { get; init; }

    public bool Failed { get; init; }
}

public sealed record ObservedResult
{
    public long Samples { get; init; }

    public long PeakDependencyInFlight { get; init; }

    public double FirstHeapMb { get; init; }

    public double PeakHeapMb { get; init; }

    public double FinalHeapMb { get; init; }

    public double FirstWorkingSetMb { get; init; }

    public double PeakWorkingSetMb { get; init; }

    public int PeakCachedReportEntries { get; init; }

    public int PeakThreadCount { get; init; }

    public int Gen2Collections { get; init; }
}

public static class ResultJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(string path, RunResult result)
    {
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(result, Options));
    }

    public static RunResult Read(string path) =>
        JsonSerializer.Deserialize<RunResult>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException($"{path} did not contain a run result");
}
