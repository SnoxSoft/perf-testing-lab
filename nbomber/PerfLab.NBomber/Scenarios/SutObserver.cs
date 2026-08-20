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
    private static double _firstHeapMb;
    private static double _peakHeapMb;
    private static double _finalHeapMb;
    private static double _firstWorkingSetMb;
    private static double _peakWorkingSetMb;
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
        double FirstHeapMb,
        double PeakHeapMb,
        double FinalHeapMb,
        double FirstWorkingSetMb,
        double PeakWorkingSetMb,
        int PeakCachedReportEntries,
        int PeakThreadCount,
        int Gen2Collections)
    {
        /// <summary>
        /// Growth from the first sample to the peak. This is the number a soak
        /// test exists to produce, and it is meaningless without the duration it
        /// was measured over — see <see cref="GrowthMbPerMinute"/>.
        /// </summary>
        public double HeapGrowthMb => PeakHeapMb - FirstHeapMb;

        /// <summary>
        /// A rate rather than a total, because a total invites the wrong
        /// conclusion. 80MB of growth is unremarkable over a week and alarming
        /// over four minutes, and only the rate lets you extrapolate to the
        /// container limit.
        ///
        /// Samples are one second apart, so the sample count is the elapsed
        /// seconds.
        /// </summary>
        public double GrowthMbPerMinute =>
            Samples > 1 ? HeapGrowthMb / (Samples / 60.0) : 0;

        /// <summary>
        /// Resident growth, which is what the container limit actually applies to.
        ///
        /// Extrapolating managed heap against the memory limit understates the
        /// risk, sometimes badly: this service starts with a 1.3MB managed heap
        /// inside an 89MB working set, so nearly all of the limit is already spent
        /// before a single byte leaks. The managed heap says what is leaking;
        /// resident memory says how long there is left.
        /// </summary>
        public double WorkingSetGrowthMb => PeakWorkingSetMb - FirstWorkingSetMb;

        public double WorkingSetGrowthMbPerMinute =>
            Samples > 1 ? WorkingSetGrowthMb / (Samples / 60.0) : 0;

        /// <summary>
        /// Minutes until resident memory reaches the container limit at the
        /// observed rate. Null when nothing is growing.
        ///
        /// This is the number a soak exists to produce. A growth rate is not a
        /// finding until it is expressed as time remaining.
        /// </summary>
        public double? MinutesToLimit(double containerLimitMb) =>
            WorkingSetGrowthMbPerMinute > 0.5
                ? Math.Max(0, containerLimitMb - PeakWorkingSetMb) / WorkingSetGrowthMbPerMinute
                : null;
    }

    public static Observation Current => new(
        Samples: Interlocked.Read(ref _samples),
        PeakDependencyInFlight: Interlocked.Read(ref _peakDependencyInFlight),
        FirstHeapMb: _firstHeapMb,
        PeakHeapMb: _peakHeapMb,
        FinalHeapMb: _finalHeapMb,
        FirstWorkingSetMb: _firstWorkingSetMb,
        PeakWorkingSetMb: _peakWorkingSetMb,
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
            double workingSetMb = (long)snapshot["workingSetBytes"]! / 1024.0 / 1024.0;

            // Plain reads and writes: the observer runs at one iteration per
            // second with a single instance, so there is never more than one
            // sampler in flight.
            if (_samples == 0)
            {
                // Baseline before load has had time to accumulate anything.
                _firstHeapMb = heapMb;
                _firstWorkingSetMb = workingSetMb;
            }

            _peakDependencyInFlight = Math.Max(_peakDependencyInFlight, inFlight);
            _peakHeapMb = Math.Max(_peakHeapMb, heapMb);
            _peakWorkingSetMb = Math.Max(_peakWorkingSetMb, workingSetMb);
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
