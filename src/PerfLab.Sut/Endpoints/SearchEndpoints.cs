namespace PerfLab.Sut.Endpoints;

public static class SearchEndpoints
{
    /// <summary>
    /// Name of the rate limiter policy applied to search. Registered in
    /// Program.cs against the ASP.NET Core rate limiting middleware, which
    /// returns a genuine 429 rather than a simulated one.
    /// </summary>
    public const string RateLimitPolicy = "search";

    public static void MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        // Rate limited on purpose. A 429 means the service refused work it
        // could not safely accept — that is the system behaving correctly, and
        // it is categorically different from a 500 or a timeout.
        //
        // Spike tests exist largely to check this distinction, and a threshold
        // that fails the run on "any non-2xx" will report a healthy service as
        // broken. Both suites here treat 429 as an expected outcome with its
        // own budget.
        app.MapGet("/api/search", (string? q) => Results.Ok(new SearchResponse(
                Query: q ?? string.Empty,
                Matches: string.IsNullOrWhiteSpace(q) ? 0 : q.Length * 3,
                Throttled: false)))
            .RequireRateLimiting(RateLimitPolicy)
            .WithTags("search")
            .WithSummary("Rate limited. Returns 429 above the configured rate — refusal, not failure.");
    }

    private sealed record SearchResponse(string Query, int Matches, bool Throttled);
}
