using System.Text.Json.Nodes;
using NBomber.Contracts;
using NBomber.CSharp;

namespace PerfLab.NBomber.Scenarios;

/// <summary>
/// A low-rate scenario that samples the service's own diagnostics.
///
/// Client-side latency describes the symptom. It cannot distinguish a service
/// that is slow because work is queued from one that is slow because the heap is
/// growing or because calls to a downstream are accumulating. Those are different
/// faults with different fixes and they produce identical latency curves.
///
/// Running the sampler as a scenario rather than a background thread means its
/// requests are scheduled by the same engine as the load, appear in the same
/// report, and stop when the run stops.
///
/// It is deliberately cheap — one request per second to an endpoint that touches
/// no database and takes no locks. An observer that perturbs what it measures is
/// worse than no observer.
///
/// Peaks are tracked here in plain static state rather than through NBomber's
/// Metric.CreateGauge API. Custom counters and gauges registered that way did not
/// appear in NodeStats.Metrics or in any of the four report formats — only
/// NBomber's own eleven process gauges did — so they appear to be intended for
/// reporting sinks rather than the run summary. That is worth revisiting when the
/// InfluxDB sink goes in; until then, static state is honest and works.
/// </summary>
public static class SutObserver
{
    private static long _peakDependencyInFlight;
    private static double _peakHeapMb;
    private static double _finalHeapMb;
    private static int _peakCachedEntries;
    private static int _peakThreadCount;
    private static int _gen2Collections;
    private static long _samples;

    /// <summary>
    /// What the server reported at its worst moment during the run.
    ///
    /// Peak rather than final, because a run ends after the load stops and the
    /// last sample is therefore taken while the system is already recovering. For
    /// a stress test the interesting moment is the worst one.
    /// </summary>
    public sealed record Observation(
        long Samples,
        long PeakDependencyInFlight,
        double PeakHeapMb,
        double FinalHeapMb,
        int PeakCachedReportEntries,
        int PeakThreadCount,
        int Gen2Collections);

    public static Observation Current => new(
        Samples: Interlocked.Read(ref _samples),
        PeakDependencyInFlight: Interlocked.Read(ref _peakDependencyInFlight),
        PeakHeapMb: _peakHeapMb,
        FinalHeapMb: _finalHeapMb,
        PeakCachedReportEntries: _peakCachedEntries,
        PeakThreadCount: _peakThreadCount,
        Gen2Collections: _gen2Collections);

    public static ScenarioProps Diagnostics(HttpClient client, string name = "observer") =>
        Scenario.Create(name, async context =>
        {
            using HttpResponseMessage response =
                await client.GetAsync("/diagnostics/memory", context.ScenarioCancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Response.Fail<string>(
                    statusCode: ((int)response.StatusCode).ToString(),
                    message: "diagnostics unavailable");
            }

            string payload = await response.Content.ReadAsStringAsync(context.ScenarioCancellationToken);
            JsonNode snapshot = JsonNode.Parse(payload)!;

            long inFlight = (long)snapshot["dependencyCallsInFlight"]!;
            double heapMb = (long)snapshot["heapBytes"]! / 1024.0 / 1024.0;

            // Plain reads and writes: the observer runs at one iteration per
            // second with a single instance, so there is never more than one
            // sampler in flight.
            _peakDependencyInFlight = Math.Max(_peakDependencyInFlight, inFlight);
            _peakHeapMb = Math.Max(_peakHeapMb, heapMb);
            _finalHeapMb = heapMb;
            _peakCachedEntries = Math.Max(_peakCachedEntries, (int)snapshot["cachedReportEntries"]!);
            _peakThreadCount = Math.Max(_peakThreadCount, (int)snapshot["threadCount"]!);
            _gen2Collections = (int)snapshot["gen2Collections"]!;
            Interlocked.Increment(ref _samples);

            return Response.Ok(payload: payload, sizeBytes: payload.Length);
        });

    /// <summary>
    /// One sample per second. No warm-up, because the observer must already be
    /// running when the load starts — otherwise the most interesting moments go
    /// unrecorded.
    /// </summary>
    public static ScenarioProps Sampling(HttpClient client, TimeSpan during, string name = "observer") =>
        Diagnostics(client, name)
            .WithoutWarmUp()
            .WithLoadSimulations(
                Simulation.Inject(rate: 1, interval: TimeSpan.FromSeconds(1), during: during));
}
