using System.Globalization;
using System.Text;

namespace PerfLab.Bench;

/// <summary>
/// Renders a summary as Markdown, for committing next to the JSON.
///
/// The JSON is what a later comparison reads; this is what a person reads. Every
/// figure is shown as median with the observed range, because a bare median from
/// three runs invites more confidence than three runs earn.
/// </summary>
public static class Markdown
{
    /// <summary>
    /// Range wider than this fraction of the median gets flagged. Ten percent is
    /// a judgement, not a law: below it the median is worth quoting, above it the
    /// runs disagreed enough that quoting one number is misleading.
    /// </summary>
    private const double NoisyThreshold = 0.10;

    public static string Render(BenchSummary summary)
    {
        StringBuilder output = new();

        output.AppendLine(CultureInfo.InvariantCulture, $"# {summary.Profile} ({summary.Tool})");
        output.AppendLine();
        output.AppendLine(CultureInfo.InvariantCulture,
            $"{summary.Repetitions} runs at scale {summary.Scale:0.##}, " +
            $"{(summary.FreshTargetPerRun ? "target restarted between runs" : "same target throughout")}. " +
            $"Outcomes: {string.Join(", ", summary.Outcomes)}.");
        output.AppendLine();
        output.AppendLine("Median with [min–max] across runs. A flagged row means the runs disagreed by more");
        output.AppendLine("than 10% of the median, so the median should not be quoted on its own.");
        output.AppendLine();

        output.AppendLine("| scenario | rps | mean ms | p50 ms | p95 ms | p99 ms | fails | |");
        output.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (AggregatedScenario scenario in summary.Scenarios)
        {
            AppendRow(output, scenario, indent: false);

            foreach (AggregatedScenario step in scenario.Steps)
            {
                AppendRow(output, step, indent: true);
            }
        }

        if (summary.Observed is { } observed)
        {
            output.AppendLine();
            output.AppendLine("## Observed on the server");
            output.AppendLine();
            output.AppendLine("| measure | median | range |");
            output.AppendLine("| --- | --- | --- |");
            AppendObservation(output, "peak dependency calls in flight", observed.PeakDependencyInFlight);
            AppendObservation(output, "managed heap growth (MB)", observed.HeapGrowthMb);
            AppendObservation(output, "working set growth (MB)", observed.WorkingSetGrowthMb);
            AppendObservation(output, "peak cached report entries", observed.PeakCachedReportEntries);
            AppendObservation(output, "gen2 collections", observed.Gen2Collections);
        }

        output.AppendLine();
        output.AppendLine("## Provenance");
        output.AppendLine();
        output.AppendLine(CultureInfo.InvariantCulture, $"- commit `{Short(summary.Provenance.GitSha)}`" +
            $"{(summary.Provenance.GitDirty ? " **(dirty working tree — not reproducible)**" : "")}");
        output.AppendLine(CultureInfo.InvariantCulture, $"- tool: {summary.Tool} {summary.ToolVersion}");
        output.AppendLine(CultureInfo.InvariantCulture,
            $"- host: {summary.Provenance.Host.OperatingSystem}, " +
            $"{summary.Provenance.Host.Architecture}, {summary.Provenance.Host.ProcessorCount} logical cores");
        output.AppendLine(CultureInfo.InvariantCulture,
            $"- target limits: {Cpus(summary.Provenance.Target.CpuLimit)}, " +
            $"{Megabytes(summary.Provenance.Target.MemoryLimitBytes)}");
        output.AppendLine(CultureInfo.InvariantCulture, $"- collected {summary.Provenance.CollectedAtUtc}");

        if (!string.IsNullOrWhiteSpace(summary.Provenance.Target.Pathologies))
        {
            output.AppendLine();
            output.AppendLine("Target configuration at the time of the run:");
            output.AppendLine();
            output.AppendLine("```json");
            output.AppendLine(summary.Provenance.Target.Pathologies.Trim());
            output.AppendLine("```");
        }

        return output.ToString();
    }

    private static void AppendRow(StringBuilder output, AggregatedScenario scenario, bool indent)
    {
        bool noisy = scenario.P95Ms.Spread > NoisyThreshold || scenario.RequestsPerSecond.Spread > NoisyThreshold;

        output.AppendLine(CultureInfo.InvariantCulture,
            $"| {(indent ? "&nbsp;&nbsp;↳ " : "")}{scenario.Name} " +
            $"| {Cell(scenario.RequestsPerSecond)} " +
            $"| {Cell(scenario.MeanMs)} " +
            $"| {Cell(scenario.P50Ms)} " +
            $"| {Cell(scenario.P95Ms)} " +
            $"| {Cell(scenario.P99Ms)} " +
            $"| {scenario.FailCount.Median:N0} " +
            $"| {(noisy ? "⚠ noisy" : "")} |");
    }

    private static void AppendObservation(StringBuilder output, string label, Stat stat) =>
        output.AppendLine(CultureInfo.InvariantCulture,
            $"| {label} | {stat.Median:N1} | {stat.Min:N1} – {stat.Max:N1} |");

    private static string Cell(Stat stat) =>
        stat.Min == stat.Max
            ? stat.Median.ToString("N2", CultureInfo.InvariantCulture)
            : $"{stat.Median:N2} [{stat.Min:N2}–{stat.Max:N2}]";

    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;

    private static string Cpus(string nanoCpus) =>
        long.TryParse(nanoCpus, out long nanos) && nanos > 0
            ? $"{nanos / 1_000_000_000.0:0.##} CPU"
            : "CPU limit unknown";

    private static string Megabytes(string bytes) =>
        long.TryParse(bytes, out long value) && value > 0
            ? $"{value / 1024 / 1024} MB"
            : "memory limit unknown";
}
