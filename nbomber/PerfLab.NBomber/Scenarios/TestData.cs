using NBomber.Contracts;

namespace PerfLab.NBomber.Scenarios;

/// <summary>
/// Test identities, and the three ways to hand them out.
///
/// NBomber 6 has no DataFeed API — earlier versions did, and it is gone — so
/// test data is plain C# indexed off the scenario context. That is arguably an
/// improvement: there is no framework abstraction hiding which of the three
/// access patterns below is in use, and they mean completely different things.
///
/// Choosing the wrong one is a common way to produce confident, wrong numbers.
/// </summary>
public static class TestData
{
    /// <summary>
    /// Deliberately more identities than there are virtual users in any profile
    /// here. A data set smaller than the VU count silently turns into shared
    /// state, and shared identities produce cache hits that a real user
    /// population would not.
    /// </summary>
    public static readonly string[] Users =
    [
        "alice", "bob", "carol", "dave", "erin", "frank", "grace", "heidi",
        "ivan", "judy", "karl", "linda", "mallory", "niaj", "olivia", "peggy",
        "quentin", "rupert", "sybil", "trent", "ursula", "victor", "wendy",
        "xavier", "yvonne", "zach",
    ];

    /// <summary>
    /// A different identity every iteration, cycling through the set.
    ///
    /// Use when each request should look like a distinct user — cache-busting,
    /// exercising per-user code paths, avoiding a hot row. This is the pattern
    /// that stops a load test from accidentally measuring one cache entry.
    ///
    /// Deterministic, so two runs offer the same sequence.
    /// </summary>
    public static string Circular(IScenarioContext context) =>
        Users[(int)(context.InvocationNumber % Users.Length)];

    /// <summary>
    /// One identity per virtual user, stable for the whole run.
    ///
    /// Required whenever the identity carries session state — a token, a cart, a
    /// connection. Cycling identities per iteration would invalidate the cached
    /// token on every request and quietly convert a correlation test into a
    /// login benchmark.
    /// </summary>
    public static string PerVirtualUser(IScenarioContext context) =>
        Users[context.ScenarioInfo.InstanceNumber % Users.Length];

    /// <summary>
    /// Uniformly random.
    ///
    /// The most realistic and the least reproducible: two runs draw different
    /// sequences, so a latency difference between them can be the data rather
    /// than the code. Fine for exploration, poor for a committed baseline —
    /// which is why nothing in this suite uses it by default.
    /// </summary>
    public static string Random(IScenarioContext context) =>
        Users[context.Random.Next(Users.Length)];
}
