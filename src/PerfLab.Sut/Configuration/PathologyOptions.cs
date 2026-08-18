namespace PerfLab.Sut.Configuration;

/// <summary>
/// Every performance pathology in this service is configurable, because a load
/// test that changes two variables at once measures neither. Flipping one flag
/// between two otherwise identical runs is what turns a number into evidence.
/// </summary>
public sealed class PathologyOptions
{
    public const string SectionName = "Pathologies";

    /// <summary>
    /// Maximum size of the Npgsql connection pool. Deliberately far below the
    /// concurrency the load tests offer, so the pool — not PostgreSQL, and not
    /// the CPU — is the first bottleneck reached. This is the clearest possible
    /// demonstration of a queue: past saturation, throughput flattens and
    /// latency climbs in direct proportion to offered load.
    /// </summary>
    public int MaxPoolSize { get; init; } = 20;

    /// <summary>
    /// How long a pooled request holds its connection, via pg_sleep. Sets the
    /// service time of the queue, and therefore the theoretical ceiling:
    /// MaxPoolSize / HoldDuration requests per second.
    /// </summary>
    public TimeSpan PooledHoldDuration { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Rows fetched individually by the N+1 endpoint. The cost is one network
    /// round trip each, which is invisible on a single request and dominant
    /// under concurrency.
    /// </summary>
    public int NPlusOneRowCount { get; init; } = 25;

    /// <summary>
    /// When true, the report cache never evicts. This is the endurance test's
    /// entire reason for existing: a bounded cache produces a flat memory
    /// graph over six hours, and a flat graph proves nothing either way.
    /// </summary>
    public bool UnboundedCache { get; init; } = true;

    /// <summary>
    /// Latency of the simulated downstream dependency. No cancellation token is
    /// honoured on that path on purpose, so a stress run can show requests
    /// piling up behind a dependency that has effectively stopped answering.
    /// </summary>
    public TimeSpan SlowDependencyLatency { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Requests per second the search endpoint accepts before returning 429.
    /// A rejected request is not a failed request, and telling those two apart
    /// is most of the skill in reading spike-test output.
    /// </summary>
    public int SearchRateLimitPerSecond { get; init; } = 50;

    /// <summary>
    /// Size of the buffer the export endpoint allocates per request. Above
    /// 85,000 bytes an array lands on the large object heap, which is not
    /// compacted by default — so the pathology is fragmentation, not just
    /// allocation volume.
    /// </summary>
    public int ExportAllocationBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Artificial delay applied to the first request only, standing in for
    /// JIT, pool fill and cache warm-up. NBomber has a first-class warm-up
    /// phase; in k6 the equivalent is arranged by hand. This flag makes the
    /// difference measurable rather than theoretical.
    /// </summary>
    public TimeSpan ColdStartPenalty { get; init; } = TimeSpan.FromSeconds(1);
}
