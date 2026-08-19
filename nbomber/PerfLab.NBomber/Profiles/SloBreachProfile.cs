using NBomber.Contracts;
using NBomber.CSharp;
using PerfLab.NBomber.Scenarios;
using PerfLab.NBomber.Thresholds;

namespace PerfLab.NBomber.Profiles;

/// <summary>
/// A deliberately failing run, kept because it is the proof that the gate works.
///
/// The capacity ladder established that pooled_queue saturates near 370 req/s.
/// This profile offers 450 req/s against exactly the same service level
/// objectives the load profile uses. The expected outcome:
///
///   failed requests:  0
///   p50 latency:      ~2500ms   (budget 70ms)
///   exit code:        1
///
/// Zero failures and a two-and-a-half second p50. Every availability metric
/// reports a perfectly healthy service; the latency thresholds fail the run.
/// That gap is the entire argument for latency thresholds, and it is far more
/// convincing as an executable demonstration than as a paragraph in a README.
///
/// It also serves as a regression test on the gate itself. A threshold that
/// never fires is indistinguishable from a threshold that cannot fire, and the
/// only way to tell the difference is to point something at it that should fail.
/// Wiring this into CI as an expected-failure case means a change that silently
/// disables the thresholds gets caught.
/// </summary>
public sealed class SloBreachProfile : IProfile
{
    public string Name => "slo-breach";

    public string Question =>
        "Does a latency SLO catch saturation that an error-rate SLO misses? (Expected to fail.)";

    public ScenarioProps[] Build(HttpClient client) =>
    [
        SutScenarios.PooledQueue(client)
            .WithPooledQueueSlo()
            .WithoutWarmUp()
            .WithLoadSimulations(
                // Open model, above the measured 370 req/s ceiling. A closed
                // model could not produce this state at all: fixed workers would
                // simply complete fewer iterations and every threshold would pass
                // while the service was equally overloaded.
                Simulation.Inject(
                    rate: 450,
                    interval: TimeSpan.FromSeconds(1),
                    during: RunLength.Seconds(30))),
    ];
}
