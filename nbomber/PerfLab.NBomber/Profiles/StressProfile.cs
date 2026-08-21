using NBomber.Contracts;
using NBomber.CSharp;
using PerfLab.NBomber.Scenarios;
using PerfLab.NBomber.Thresholds;

namespace PerfLab.NBomber.Profiles;

/// <summary>
/// Past capacity on purpose, to characterise *how* the service fails.
///
/// A load test asks whether service levels hold. A capacity test asks where the
/// knee is. A stress test asks what neither can: when this breaks, does it
/// degrade, shed load, or fall over — and does it come back? "It broke" is not
/// worth an afternoon; the failure *mode* is.
///
/// This profile is a controlled experiment with two arms, both hitting the same
/// two second downstream dependency at the same rates. The single variable is
/// whether a pooled connection is held across the wait.
///
///   arm A  /enrich          touches no pool
///   arm B  /enrich-holding  holds one connection for the whole call
///
/// Arm A was where the original version of this profile went wrong. It was
/// written expecting unbounded in-flight growth to cause a cascading failure,
/// and it did not: 50 through 400 rps produced a flat 2003ms mean with zero
/// failures throughout, because a pending Task.Delay is a timer entry rather
/// than a resource. Hundreds of concurrent in-flight calls are close to free on
/// an async stack, and the textbook thread-per-request cascade simply does not
/// occur.
///
/// Arm B is where it does. Holding a connection turns the arithmetic into:
///
///   ceiling = pool size / dependency latency = 20 / 2s = 10 req/s
///
/// Forty times lower than arm A's offered rate, from an identical dependency.
///
/// The conclusion is more useful than either arm alone: in-flight count does not
/// determine whether a slow dependency is survivable. What determines it is
/// whether that in-flight work holds something scarce. Which in turn is the
/// concrete argument for scoping a timeout tighter than the resource it borrows,
/// rather than for adding a timeout in the abstract.
/// </summary>
public sealed class StressProfile : IProfile
{
    private static readonly int[] RatesPerSecond = [25, 50, 100, 200];

    public string Name => "stress";

    public string Question =>
        "Past capacity, how does it fail? (Same dependency, with and without holding a connection.)";

    /// <summary>
    /// Exceeding capacity is the point, so timeouts here are the result rather
    /// than a broken test. Arm B is expected to fail heavily.
    /// </summary>
    public bool FailOnErrors => false;

    public ScenarioProps[] Build(HttpClient client)
    {
        TimeSpan step = RunLength.Seconds(20);
        TimeSpan lead = RunLength.Seconds(5);
        TimeSpan armGap = RunLength.Seconds(20);

        // Arm A runs first, then a gap for the pool to drain, then arm B. They
        // cannot overlap: both draw on the same pool, and simultaneous arms would
        // make each one's result partly caused by the other.
        TimeSpan armAStartsAt = lead;
        TimeSpan armBStartsAt = lead + (step * RatesPerSecond.Length) + armGap;

        TimeSpan total = armBStartsAt + (step * RatesPerSecond.Length) + RunLength.Seconds(30);

        List<ScenarioProps> scenarios = [SutObserver.Sampling(client, total)];

        for (int i = 0; i < RatesPerSecond.Length; i++)
        {
            int rate = RatesPerSecond[i];

            // Arm A carries an objective; arm B deliberately does not.
            //
            // The whole finding is that unbounded async in-flight work is harmless,
            // so arm A failing is a regression worth stopping for: it would mean
            // something now bounds a path that previously had no limit. Arm B is
            // expected to fail heavily — asserting anything there would fail by
            // design.
            scenarios.Add(
                Rung(
                    SutScenarios.SlowDependency(client, $"A_free_{i + 1:00}_at_{rate}rps")
                        .WithNoFailures(),
                    startAt: armAStartsAt + (step * i), rate: rate, during: step));

            scenarios.Add(
                Rung(
                    SutScenarios.SlowDependencyHolding(client, $"B_holding_{i + 1:00}_at_{rate}rps"),
                    startAt: armBStartsAt + (step * i), rate: rate, during: step));
        }

        return [.. scenarios];
    }

    private static ScenarioProps Rung(
        ScenarioProps scenario,
        TimeSpan startAt,
        int rate,
        TimeSpan during) =>
        scenario
            .WithoutWarmUp()
            .WithLoadSimulations(
                Simulation.Pause(startAt),
                Simulation.Inject(rate: rate, interval: TimeSpan.FromSeconds(1), during: during));
}
