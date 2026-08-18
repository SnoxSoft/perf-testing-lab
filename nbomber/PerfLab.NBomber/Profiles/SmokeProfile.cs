using NBomber.Contracts;
using NBomber.CSharp;
using PerfLab.NBomber.Scenarios;

namespace PerfLab.NBomber.Profiles;

/// <summary>
/// One virtual user, a handful of iterations, every scenario.
///
/// A smoke test does not test the system — it tests the test. It answers "does
/// every scenario compile, connect, authenticate, parse and assert correctly",
/// and it answers it in under a minute rather than discovering a typo forty
/// minutes into a capacity ramp.
///
/// This is the profile that belongs on every pull request. The expensive shapes
/// do not.
/// </summary>
public sealed class SmokeProfile : IProfile
{
    public string Name => "smoke";

    public string Question => "Does every scenario actually work? (Validates the test, not the system.)";

    public ScenarioProps[] Build(HttpClient client) =>
    [
        // IterationsForConstant gives an exact, bounded amount of work rather
        // than a duration, so a smoke run takes the same time on a fast laptop
        // and a slow CI agent. Duration-based shapes belong in the profiles
        // that are actually measuring something.
        Fixed(SutScenarios.GeneratorCeiling(client), iterations: 20),
        Fixed(SutScenarios.Baseline(client), iterations: 10),
        Fixed(SutScenarios.NPlusOneComparison(client), iterations: 10),
        Fixed(SutScenarios.PooledQueue(client), iterations: 10),
        Fixed(SutScenarios.LockContention(client), iterations: 10),

        // Three iterations, not ten: this endpoint waits two seconds by design
        // and a smoke test has no reason to spend thirty seconds proving it.
        Fixed(SutScenarios.SlowDependency(client), iterations: 3),

        Fixed(SutScenarios.UniqueReports(client), iterations: 10),
        Fixed(SutScenarios.RepeatedReports(client), iterations: 10),
        Fixed(SutScenarios.AllocationPressure(client), iterations: 10),
        Fixed(SutScenarios.RateLimitedSearch(client), iterations: 10),
    ];

    /// <summary>
    /// A single virtual user, no warm-up.
    ///
    /// Warm-up is skipped on purpose here. It exists to keep cold-start samples
    /// out of the percentiles of a measured run, and a smoke test measures
    /// nothing — so paying for it would only make the fastest feedback loop in
    /// the suite slower.
    /// </summary>
    private static ScenarioProps Fixed(ScenarioProps scenario, int iterations) =>
        scenario
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.IterationsForConstant(copies: 1, iterations: iterations));
}
