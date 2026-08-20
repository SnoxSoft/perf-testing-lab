using NBomber.Contracts;
using NBomber.CSharp;
using PerfLab.NBomber.Scenarios;

namespace PerfLab.NBomber.Profiles;

/// <summary>
/// Moderate, unremarkable load held for a long time. Duration is the variable.
///
/// This is the only shape where the load itself is uninteresting. Nothing here
/// is near capacity: the rate is well inside what the load profile already
/// proved comfortable. The question is entirely about time, and it is the one
/// question no other shape can answer — what degrades only after hours?
///
/// Leaks, heap fragmentation, connection churn, log volume, cache growth,
/// gradually widening GC pauses. All of them are invisible in a five minute run
/// and all of them page someone at 4am on the third day.
///
/// Two arms, run sequentially, differing in one thing:
///
///   arm A  unique keys     every request retains ~8KB forever
///   arm B  repeated key    identical work, one cache entry
///
/// They cannot overlap. Heap is a single shared number, so simultaneous arms
/// would make it impossible to attribute growth to either. That constraint is
/// general: memory experiments have to be serialised in a way latency
/// experiments do not.
///
/// Each arm is split into consecutive windows registered as separate scenarios,
/// which turns NBomber's per-scenario summary into a latency-over-time table.
/// This matters more here than anywhere else, because a soak whose result is a
/// single aggregate p99 has thrown away the only dimension it was measuring. The
/// finding is not "p99 was 40ms", it is "p99 in the last window was four times
/// the first".
///
/// On duration: PERFLAB_SCALE multiplies, so it scales up as readily as down.
/// The default here is a short soak that fits in a coffee break; a real one
/// would be PERFLAB_SCALE=20 overnight. What must not change between them is the
/// *shape*, which is why the scale factor exists instead of a second set of
/// numbers.
/// </summary>
public sealed class EnduranceProfile : IProfile
{
    private const int Windows = 4;

    /// <summary>
    /// Deliberately modest — roughly a seventh of the pool endpoint's measured
    /// capacity. If a soak needs high load to show a problem, it is not a soak.
    /// </summary>
    private const int RatePerSecond = 50;

    public string Name => "endurance";

    public string Question => "What degrades only with time? (Unique keys leak, repeated keys do not.)";

    /// <summary>
    /// Growth budget for the whole run, checked against the observer.
    ///
    /// This is the closest thing a soak has to a service level objective, and it
    /// cannot be expressed as an NBomber threshold because it is a server-side
    /// number the client never sees. Latency thresholds would not catch this at
    /// all until the heap was large enough to affect GC pauses — by which point
    /// the container is minutes from being killed.
    /// </summary>
    public double? HeapGrowthBudgetMb => 60;

    public ScenarioProps[] Build(HttpClient client)
    {
        TimeSpan window = RunLength.Minutes(2);
        TimeSpan lead = RunLength.Seconds(10);

        // A gap between the arms, with the observer still sampling. Whether the
        // heap falls back during an idle period is the diagnostic that separates
        // a leak from mere caching: a bounded cache under pressure releases, and
        // a leak does not.
        TimeSpan settle = RunLength.Seconds(30);

        TimeSpan armAStartsAt = lead;
        TimeSpan armBStartsAt = lead + (window * Windows) + settle;
        TimeSpan total = armBStartsAt + (window * Windows) + RunLength.Seconds(20);

        List<ScenarioProps> scenarios = [SutObserver.Sampling(client, total)];

        for (int i = 0; i < Windows; i++)
        {
            scenarios.Add(
                Window(
                    SutScenarios.UniqueReports(client, $"A_unique_w{i + 1}"),
                    startAt: armAStartsAt + (window * i),
                    during: window));

            scenarios.Add(
                Window(
                    SutScenarios.RepeatedReports(client, $"B_repeated_w{i + 1}"),
                    startAt: armBStartsAt + (window * i),
                    during: window));
        }

        return [.. scenarios];
    }

    /// <summary>
    /// Open model even though the load is modest, because a closed model would
    /// quietly reduce the offered rate as the service degraded — turning the
    /// symptom into a self-correcting feedback loop and hiding exactly the trend
    /// the run exists to measure.
    /// </summary>
    private static ScenarioProps Window(ScenarioProps scenario, TimeSpan startAt, TimeSpan during) =>
        scenario
            .WithoutWarmUp()
            .WithLoadSimulations(
                Simulation.Pause(startAt),
                Simulation.Inject(
                    rate: RatePerSecond,
                    interval: TimeSpan.FromSeconds(1),
                    during: during));
}
