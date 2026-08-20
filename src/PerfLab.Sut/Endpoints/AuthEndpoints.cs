using System.Diagnostics;
using PerfLab.Sut.Services;

namespace PerfLab.Sut.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Expensive by design. A load test that calls this once per iteration is
        // measuring the login path, not the endpoint it thinks it is testing.
        app.MapPost("/api/auth/token", async (string user, TokenIssuer issuer) =>
        {
            long startedAt = Stopwatch.GetTimestamp();
            (string token, int expiresInSeconds) = await issuer.IssueAsync(user);

            return Results.Ok(new TokenResponse(
                Token: token,
                ExpiresInSeconds: expiresInSeconds,
                IssuanceMs: Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 2)));
        })
        .WithTags("auth")
        .WithSummary("Issues a bearer token. Deliberately costly, like a real password hash.");

        // Cheap by design, and the endpoint a correlation test should actually be
        // measuring. Returns 401 rather than 403 for a missing or expired token,
        // so a test can distinguish "not authenticated" from "not allowed".
        app.MapGet("/api/orders/mine", (
            HttpRequest request,
            TokenIssuer issuer) =>
        {
            string? header = request.Headers.Authorization.FirstOrDefault();
            string? token = header?.StartsWith("Bearer ", StringComparison.Ordinal) == true
                ? header["Bearer ".Length..]
                : null;

            string? user = issuer.Validate(token);

            if (user is null)
            {
                // A 401 here is a correlation bug in the test far more often than
                // a fault in the service: an unextracted token, a token cached
                // past its expiry, or a header assembled wrongly. Worth keeping
                // distinct from every other failure so it is obvious which.
                return Results.Unauthorized();
            }

            // Derived from the user so a test can assert it received *its own*
            // data. A correlation test that never checks whose data came back
            // will not notice VUs sharing one identity.
            int orderCount = 3 + (Math.Abs(user.GetHashCode(StringComparison.Ordinal)) % 5);

            return Results.Ok(new OrdersResponse(
                User: user,
                OrderCount: orderCount,
                Orders: Enumerable.Range(1, orderCount)
                    .Select(i => new OrderView($"{user}-order-{i}", i * 19.99m))
                    .ToArray()));
        })
        .WithTags("auth")
        .WithSummary("Requires a valid bearer token. Cheap: this is what correlation tests measure.");
    }

    private sealed record TokenResponse(string Token, int ExpiresInSeconds, double IssuanceMs);

    private sealed record OrderView(string Reference, decimal Amount);

    private sealed record OrdersResponse(string User, int OrderCount, OrderView[] Orders);
}
