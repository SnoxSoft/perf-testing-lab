namespace PerfLab.Results;

/// <summary>
/// What a suite reports about itself when asked, via <c>--list</c>.
///
/// The bench aggregator needs to know things about a profile *before* running it
/// — most importantly whether repeated runs require a fresh target. Hard-coding
/// that list in the harness would mean two places to update and one of them
/// silently going stale, so the suite describes itself instead.
/// </summary>
public sealed record ProfileCatalog
{
    public required string Tool { get; init; }

    public required string ToolVersion { get; init; }

    public IReadOnlyList<ProfileInfo> Profiles { get; init; } = [];
}

public sealed record ProfileInfo
{
    public required string Name { get; init; }

    public required string Question { get; init; }

    /// <summary>
    /// False for exploratory shapes that are designed to exceed capacity, where
    /// timeouts are the result rather than a defect.
    /// </summary>
    public bool FailOnErrors { get; init; }

    public double? HeapGrowthBudgetMb { get; init; }

    public bool RequiresFreshTarget { get; init; }
}
