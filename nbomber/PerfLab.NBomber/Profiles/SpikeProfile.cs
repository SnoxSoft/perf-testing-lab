using NBomber.Contracts;
using NBomber.CSharp;
using PerfLab.NBomber.Scenarios;
using PerfLab.NBomber.Thresholds;

namespace PerfLab.NBomber.Profiles;

/// <summary>
/// Instant burst, then back to normal. The subject is recovery, not the burst.
///
/// Everyone tests that a spike causes damage. The question that matters
/// operationally is whether the service returns to its former latency once the
/// spike passes, and how long that takes. A system that recovers in five seconds
/// and one that stays degraded for ten minutes behave identically during the
/// spike itself.
///
/// The shape is three plateaus at the same steady rate either side of a burst:
///
///   before   100 rps   comfortably under the 370 rps ceiling
///   burst   1200 rps   roughly 3x the ceiling
///   after    100 rps   identical to before
///
/// Because "before" and "after" offer exactly the same load, their latencies are
/// directly comparable, and the comparison is the entire result. If "after"
/// matches "before", the service recovered. If it does not, something is still
/// draining — a queue, a thread pool, a retry storm — and that is a far more
/// serious finding than the burst latency itself.
///
/// Running them as separate staggered scenarios rather than one scenario with
/// three simulations is what makes this readable: NBomber reports each plateau
/// separately, so the before/after comparison appears directly in the summary
/// instead of having to be recovered from a time series.
///
/// The rate limited endpoint is included because it is the counter-example. It is
/// *designed* to survive a spike by refusing work, so its latency should barely
/// move while its 429 count climbs. Any gate that treats 429 as a failure would
/// report the one endpoint that handled the spike correctly as the one that
/// broke.
/// </summary>
public sealed class SpikeProfile : IProfile
{
    public string Name => "spike";

    public string Question => "After a 3x burst, does latency return to what it was?";

    /// <summary>
    /// The burst is expected to exceed capacity. Errors during it are the
    /// subject, not a broken test — the assertion that matters is about the
    /// recovery plateau, and that is checked by comparing it to the baseline
    /// plateau rather than by an error count.
    /// </summary>
    public bool FailOnErrors => false;

    public ScenarioProps[] Build(HttpClient client)
    {
        TimeSpan steady = RunLength.Seconds(25);
        TimeSpan burst = RunLength.Seconds(10);
        TimeSpan lead = RunLength.Seconds(5);

        // Recovery gets longer than the burst that caused it, because a queue
        // takes longer to drain than it took to fill.
        TimeSpan recovery = RunLength.Seconds(40);
        TimeSpan total = lead + steady + burst + recovery + RunLength.Seconds(5);

        TimeSpan burstStartsAt = lead + steady;
        TimeSpan recoveryStartsAt = burstStartsAt + burst;

        return
        [
            SutObserver.Sampling(client, total),

            Plateau(
                SutScenarios.PooledQueue(client, "pool_1_before"),
                startAt: lead, rate: 100, during: steady),

            Plateau(
                SutScenarios.PooledQueue(client, "pool_2_burst"),
                startAt: burstStartsAt, rate: 1_200, during: burst),

            // Same rate as "before". Any difference is the cost of the spike.
            Plateau(
                SutScenarios.PooledQueue(client, "pool_3_after"),
                startAt: recoveryStartsAt, rate: 100, during: recovery),

            // The endpoint that is supposed to shrug this off, and the only part of
            // this profile that carries objectives.
            //
            // The pool plateaus above deliberately assert nothing. Whether they
            // recover depends on how much backlog the generator managed to build,
            // which differs by tool — NBomber injects the full rate regardless and
            // left p50 at 7323ms, k6 dropped what it could not deliver and recovered
            // p50 completely. Gating on that would encode one tool's load model as
            // a requirement on the service.
            //
            // Load shedding is different: it should hold under any generator, so
            // these are real requirements.
            Plateau(
                SutScenarios.RateLimitedSearch(client, "search_1_before")
                    .WithHealthy(p50BudgetMs: 10, p99BudgetMs: 40),
                startAt: lead, rate: 40, during: steady),

            Plateau(
                // Rejecting should stay cheap even at thirty times the limit. If
                // this budget is breached the limiter has started queueing rather
                // than refusing, which converts a visible 429 into an invisible
                // slowdown.
                SutScenarios.RateLimitedSearch(client, "search_2_burst")
                    .WithHealthy(p50BudgetMs: 50, p99BudgetMs: 1_500),
                startAt: burstStartsAt, rate: 1_200, during: burst),

            Plateau(
                // The recovery assertion. Same offered rate as "before", so the
                // budget is the same: a protected endpoint should be indistinguishable
                // from its pre-spike self.
                SutScenarios.RateLimitedSearch(client, "search_3_after")
                    .WithHealthy(p50BudgetMs: 10, p99BudgetMs: 40),
                startAt: recoveryStartsAt, rate: 40, during: recovery),
        ];
    }

    private static ScenarioProps Plateau(
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
