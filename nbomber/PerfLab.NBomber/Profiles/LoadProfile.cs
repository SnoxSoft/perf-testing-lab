using NBomber.Contracts;
using NBomber.CSharp;
using PerfLab.NBomber.Scenarios;

namespace PerfLab.NBomber.Profiles;

/// <summary>
/// Steady state at expected peak, closed workload model.
///
/// The question is narrow on purpose: *given the traffic we expect, do we meet
/// our service levels?* Not "where does it break" — that is the stress profile —
/// and not "where is the knee", which is the capacity profile. A load test that
/// wanders past saturation has stopped answering its own question.
///
/// Closed model (Simulation.KeepConstant) means a fixed number of virtual users,
/// each looping request → await response → request. Offered load self-throttles:
/// if the service slows down, fewer requests are sent. That is the right model
/// here because this profile represents a bounded internal caller, and it is the
/// wrong model for public traffic — see StressProfile, which uses Inject for
/// exactly that reason.
///
/// Consequence worth internalising: this shape *cannot* produce a backlog. If
/// you want to know what happens when arrivals ignore your response times, a
/// closed model will never tell you.
/// </summary>
public sealed class LoadProfile : IProfile
{
    /// <summary>
    /// The connection pool budget, and the reason the copy counts below look
    /// conservative.
    ///
    /// Every database-backed scenario draws from the same 20-connection Npgsql
    /// pool, so their capacities are not independent. Holding times differ by
    /// two orders of magnitude:
    ///
    ///   pooled_queue   ~50ms  (pg_sleep, one connection held throughout)
    ///   n_plus_one     ~18ms  (26 sequential queries on one connection)
    ///   baseline        ~1ms  (single indexed join)
    ///
    /// At 6 copies each the peak simultaneous demand is roughly 6 + 6 + 6 = 18
    /// against a pool of 20. Raising any one of them past that turns a load test
    /// into an unintentional stress test, and the resulting latency would be
    /// attributed to the wrong endpoint.
    ///
    /// This is not an artefact of the lab. Shared pools are why endpoint capacity
    /// measured in isolation does not add up in production, and why a load test
    /// of one endpoint at a time can pass while the combined mix fails.
    /// </summary>
    private const int DatabaseBackedCopies = 6;

    public string Name => "load";

    public string Question => "At expected peak, do we meet our service levels?";

    /// <summary>
    /// The copy counts are sized against measured CPU headroom, not guessed.
    ///
    /// The first version of this profile included generator_ceiling at 20 copies
    /// and repeated_reports at 8. Those two trivial endpoints produced roughly
    /// 15,000 req/s between them and drove the container to 173-193% of its
    /// 200% CPU limit. Everything else in the run was then measuring CPU
    /// starvation: pooled_queue reported p50 = 84ms against a 50ms hold, and the
    /// extra 34ms had nothing to do with the connection pool it was supposed to
    /// be exercising.
    ///
    /// Two rules came out of that:
    ///
    /// 1. A load profile is only valid if the target has headroom. Verify with
    ///    `docker stats` — 40-60% of the CPU limit is a steady state; above ~85%
    ///    the run is a stress test wearing a load test's name.
    /// 2. A headroom check cannot be a co-tenant of the mix it is checking.
    ///    Measuring the generator ceiling belongs in its own run against
    ///    /api/echo, which is what the capacity profile does.
    /// </summary>
    public ScenarioProps[] Build(HttpClient client) =>
    [
        Steady(SutScenarios.Baseline(client), copies: 2),
        Steady(SutScenarios.NPlusOneComparison(client), copies: 3),
        Steady(SutScenarios.PooledQueue(client), copies: DatabaseBackedCopies),

        // No database involvement, so this is bounded by the global lock rather
        // than the pool. The critical section is serialised at 5ms, which caps
        // throughput near 200/s no matter how many callers arrive — so even 2
        // copies sit past the point where queueing begins. p50 stays healthy and
        // p99 is where the queue becomes visible.
        Steady(SutScenarios.LockContention(client), copies: 2),

        // repeated_reports is deliberately absent, and the reason is the most
        // useful thing in this file.
        //
        // At 2 copies that 1ms endpoint produced 1696 req/s — more than every
        // other scenario here combined — and together with a larger baseline it
        // held the container at 87-94% CPU. In a closed model throughput is an
        // *output* of concurrency and latency, not something you set: one copy of
        // a fast endpoint generates more traffic than six copies of a slow one.
        //
        // So a closed model cannot express "60% catalog reads, 30% reports, 10%
        // reservations". Attempting a realistic mix this way means either
        // saturating the target with trivial requests or dropping the fast
        // endpoints. That limitation is exactly why the capacity profile uses
        // Simulation.Inject, where arrival rate is the input.
    ];

    /// <summary>
    /// Fixed concurrency for a fixed duration, after a real warm-up.
    ///
    /// Warm-up is the difference between this profile and the smoke profile, and
    /// it is worth the ten seconds: without it the first request of each scenario
    /// contributes a JIT-dominated sample of 130ms or so, which in a short run is
    /// large enough to *be* p95.
    /// </summary>
    private static ScenarioProps Steady(ScenarioProps scenario, int copies) =>
        scenario
            .WithWarmUpDuration(RunLength.WarmUp)
            .WithLoadSimulations(
                Simulation.KeepConstant(copies: copies, during: RunLength.Minutes(1)));
}
