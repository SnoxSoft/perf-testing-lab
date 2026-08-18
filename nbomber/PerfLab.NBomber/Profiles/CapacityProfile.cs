using NBomber.Contracts;
using NBomber.CSharp;
using PerfLab.NBomber.Scenarios;

namespace PerfLab.NBomber.Profiles;

/// <summary>
/// A stepped arrival-rate ladder against one endpoint, to find the knee.
///
/// Open workload model. Simulation.Inject holds a fixed arrival rate regardless
/// of whether the service is keeping up, so once the offered rate exceeds
/// capacity the backlog grows and latency climbs without bound. A closed model
/// physically cannot show this: fixed workers wait for responses, so they send
/// less exactly when the service slows down. That is why capacity has to be
/// measured with an open model even when the closed one is easier to reason
/// about.
///
/// Why a ladder of discrete steps rather than a continuous RampingInject:
/// a continuous ramp smears every arrival rate together, so a p99 from the run
/// cannot be attributed to any particular rate. Discrete plateaus give one
/// stable (rate, latency, throughput) triple per step, which is what a knee
/// actually is. RampingInject earns its place where the *transition* is the
/// subject — spike recovery, breakpoint hunting — not here.
///
/// Each step is a separate NBomber scenario, staggered with Simulation.Pause
/// (the equivalent of k6's startTime). The payoff is that NBomber's per-scenario
/// summary becomes the capacity table directly, with no post-processing:
///
///   pooled_queue_at_300   rps = 299   p50 = 53ms    p99 = 70ms
///   pooled_queue_at_400   rps = 395   p50 = 98ms    p99 = 210ms   &lt;- knee
///   pooled_queue_at_450   rps = 398   p50 = 350ms   p99 = 900ms
///
/// Throughput flattening while latency climbs *is* saturation. Both columns are
/// needed: throughput alone looks like a plateau, latency alone looks like a
/// slow service.
/// </summary>
public sealed class CapacityProfile : IProfile
{
    /// <summary>
    /// Builds the scenario under a caller-supplied name, so each rung of the
    /// ladder can be registered as a distinct scenario.
    /// </summary>
    public delegate ScenarioProps ScenarioFactory(HttpClient client, string name);

    private readonly string _endpoint;
    private readonly ScenarioFactory _factory;
    private readonly int[] _ratesPerSecond;
    private readonly int _warmUpRate;

    public CapacityProfile(
        string name,
        string endpoint,
        ScenarioFactory factory,
        int[] ratesPerSecond,
        int warmUpRate,
        string predictedCeiling)
    {
        Name = name;
        _endpoint = endpoint;
        _factory = factory;
        _ratesPerSecond = ratesPerSecond;
        _warmUpRate = warmUpRate;
        Question = $"Where is the knee for {endpoint}? Predicted ceiling: {predictedCeiling}";
    }

    public string Name { get; }

    public string Question { get; }

    /// <summary>
    /// Failures past the knee are the finding, not a broken test.
    ///
    /// A capacity ramp is supposed to end above capacity. Requests queue, some
    /// exceed the client timeout, and NBomber records them as failures. Exiting
    /// non-zero for that would make the profile unusable — the whole point is to
    /// walk off the edge and see where it was.
    /// </summary>
    public bool FailOnErrors => false;

    public ScenarioProps[] Build(HttpClient client)
    {
        TimeSpan step = RunLength.Seconds(20);
        TimeSpan warmUp = RunLength.Seconds(15);

        List<ScenarioProps> ladder =
        [
            // An explicit warm-up scenario rather than WithWarmUpDuration.
            //
            // NBomber runs each scenario's built-in warm-up at t=0, in parallel.
            // With a staggered ladder that would put warm-up traffic from every
            // later rung on the wire while the first rung is being measured. One
            // low-rate scenario that finishes before the ladder starts keeps the
            // warm-up where it belongs. Its row in the report is discarded.
            _factory(client, $"{_endpoint}_00_warmup")
                .WithoutWarmUp()
                .WithLoadSimulations(
                    Simulation.Inject(
                        rate: _warmUpRate,
                        interval: TimeSpan.FromSeconds(1),
                        during: warmUp)),
        ];

        for (int i = 0; i < _ratesPerSecond.Length; i++)
        {
            int rate = _ratesPerSecond[i];

            // Zero-padded index keeps the report rows in ladder order rather
            // than lexicographic order, so the knee reads top to bottom.
            ladder.Add(
                _factory(client, $"{_endpoint}_{i + 1:00}_at_{rate}rps")
                    .WithoutWarmUp()
                    .WithLoadSimulations(
                        Simulation.Pause(warmUp + step * i),
                        Simulation.Inject(
                            rate: rate,
                            interval: TimeSpan.FromSeconds(1),
                            during: step)));
        }

        return [.. ladder];
    }

    /// <summary>
    /// The connection pool queue, and the best teaching case in the suite
    /// because its ceiling is known before the test runs:
    ///
    ///   ceiling = pool size / hold time = 20 / 0.05s = 400 req/s
    ///
    /// A capacity test whose answer you can derive first is how you validate the
    /// method. If the measured knee is not near 400, the test is wrong before
    /// the service is.
    /// </summary>
    public static CapacityProfile PooledQueue() => new(
        name: "capacity-pool",
        endpoint: "pooled_queue",
        factory: SutScenarios.PooledQueue,
        ratesPerSecond: [100, 200, 300, 350, 400, 450, 500],
        warmUpRate: 50,
        predictedCeiling: "400 rps (20 connections / 50ms hold)");

    /// <summary>
    /// The global lock: a single-server queue rather than the pool's twenty.
    /// Nominal ceiling is 1 / critical section = 1 / 0.005s = 200 req/s, but the
    /// observed service time is nearer 6.5ms, putting the real ceiling around
    /// 154 rps. Unlike the pool this does not improve with more cores, because
    /// the work is serialised rather than merely limited.
    ///
    /// The ladder deliberately stops at 175 rps. Earlier rungs at 200-250 rps
    /// pushed the endpoint into collapse and the measurements stopped being
    /// meaningful — reported "ok" latencies exceeded the 60 second client
    /// timeout, which cannot be true of a request that did not time out. Under
    /// an open model each arrival past capacity spawns another virtual user, so
    /// deep overload saturates the *generator*, and beyond that point the numbers
    /// describe the harness rather than the service.
    ///
    /// A capacity ladder should bracket the ceiling, not vanish over it. The
    /// useful data is at the knee and just past it.
    /// </summary>
    public static CapacityProfile LockContention() => new(
        name: "capacity-lock",
        endpoint: "lock_contention",
        factory: SutScenarios.LockContention,
        ratesPerSecond: [25, 50, 75, 100, 125, 150, 175],
        warmUpRate: 15,
        predictedCeiling: "~154 rps (serialised critical section, ~6.5ms observed)");

    /// <summary>
    /// The measurement ceiling for the whole lab: generator plus framework, with
    /// no database, no locks and no allocation.
    ///
    /// This is not a test of the service. It is the number every other result has
    /// to be read against. Any endpoint measured within an order of magnitude of
    /// this figure is partly reporting the harness rather than the system, and
    /// this run is what tells you which side of that line you are on.
    ///
    /// Measured: 10,000 rps at 6.65ms mean with no failures. Strain first appears
    /// at 12,000 rps (23.3ms mean, 1258ms max) and the 14,000 rps rung comes back
    /// non-monotonically lower, which is itself the signal that the harness rather
    /// than the service is being measured up there.
    ///
    /// Against that ceiling the pathology ladders are comfortably trustworthy:
    /// the pool saturates around 370 rps (27x lower) and the lock around 154 rps
    /// (65x lower). Neither is anywhere near the measurement floor.
    /// </summary>
    public static CapacityProfile Ceiling() => new(
        name: "ceiling",
        endpoint: "echo",
        factory: SutScenarios.GeneratorCeiling,
        ratesPerSecond: [2_000, 4_000, 6_000, 8_000, 10_000, 12_000, 14_000],
        warmUpRate: 1_000,
        predictedCeiling: "~10,000 rps at 6.65ms; strain from 12,000 rps");
}
