using NBomber.Contracts;
using NBomber.CSharp;
using PerfLab.NBomber.Scenarios;

namespace PerfLab.NBomber.Profiles;

/// <summary>
/// The cost of getting correlation wrong, measured.
///
/// Both arms hit the same protected endpoint with the same identities under the
/// same closed-model load. The only difference is how often they authenticate:
/// every iteration, or once per virtual user with a refresh before expiry.
///
/// Closed model is required rather than preferred. Per-virtual-user token
/// caching lives in ScenarioInstanceData, which persists across a virtual user's
/// iterations — under Simulation.Inject every arrival is a fresh instance, the
/// cache is empty every time, and the cached arm silently becomes the naive one.
/// Nothing in the output would say so, which is what makes it worth stating in
/// the code.
///
/// Read the step latencies, not the scenario latencies. Both arms include 20ms of
/// think time outside the steps, so scenario latency carries the pacing while
/// step latency carries the service. An SLO written against the scenario total
/// would move whenever somebody adjusted the think time.
///
/// The auth step's request *count* is the headline number. In the naive arm it
/// equals the orders count; in the cached arm it should be close to the virtual
/// user count for the whole run.
/// </summary>
public sealed class CorrelationProfile : IProfile
{
    private const int VirtualUsers = 10;

    public string Name => "correlation";

    public string Question =>
        "What does authenticating every iteration cost? (Same work, token per iteration vs per user.)";

    public ScenarioProps[] Build(HttpClient client)
    {
        TimeSpan duration = RunLength.Seconds(45);
        TimeSpan gap = RunLength.Seconds(10);

        // Sequential arms. The token issuance path costs real CPU on a two-core
        // container, so running both at once would let the naive arm's login
        // traffic inflate the cached arm's latency.
        return
        [
            AuthScenarios.Naive(client)
                .WithWarmUpDuration(RunLength.WarmUp)
                .WithLoadSimulations(
                    Simulation.KeepConstant(copies: VirtualUsers, during: duration)),

            AuthScenarios.Cached(client)
                .WithoutWarmUp()
                .WithLoadSimulations(
                    Simulation.Pause(RunLength.WarmUp + duration + gap),
                    Simulation.KeepConstant(copies: VirtualUsers, during: duration)),
        ];
    }
}
