using System.Text.Json.Nodes;
using NBomber.Contracts;
using NBomber.CSharp;

namespace PerfLab.NBomber.Scenarios;

/// <summary>
/// Correlation: extract a value from one response, use it in the next.
///
/// Two arms that differ only in how often they authenticate. Both hit the same
/// protected endpoint with the same identities under the same load shape.
///
///   naive   issues a token every iteration
///   cached  issues a token once per virtual user and refreshes before expiry
///
/// The naive version is not a strawman. It is what a correlation test looks like
/// when written the obvious way, and it is wrong for a reason worth internalising:
/// authentication is expensive by design in every real system, so paying it per
/// iteration means most of the load is login traffic and the endpoint under test
/// is reported as far slower than it is.
/// </summary>
public static class AuthScenarios
{
    private const string TokenKey = "perflab.token";

    /// <summary>
    /// Refresh this far ahead of expiry.
    ///
    /// Not zero, because a token that is valid when checked can expire in flight
    /// and return 401 — a failure that appears at low rates, intermittently, and
    /// looks like a service fault rather than a test bug. Refreshing early is
    /// cheaper than diagnosing that.
    /// </summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(10);

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Authenticates on every iteration. The step split is what exposes the cost.
    /// </summary>
    public static ScenarioProps Naive(HttpClient client, string name = "auth_naive") =>
        Scenario.Create(name, async context =>
        {
            string user = TestData.PerVirtualUser(context);

            Response<string> issued = await Step.Run("auth", context, () =>
                IssueTokenAsync(client, user));

            if (issued.StatusCode is not "200")
            {
                return Response.Fail<string>(statusCode: issued.StatusCode, message: "token issuance failed");
            }

            string token = ReadToken(issued.Payload.Value);

            await Step.Run("orders", context, () => GetOrdersAsync(client, token, user));

            // Think time. Real users pause between actions, and a scenario with
            // no pacing measures a load pattern nobody has. Placed outside the
            // steps so step latencies stay clean — scenario latency includes it,
            // step latency does not, and the SLO belongs on the step.
            await Task.Delay(ThinkTime, context.ScenarioCancellationToken);

            return Response.Ok();
        });

    /// <summary>
    /// Authenticates once per virtual user, refreshing before expiry.
    ///
    /// The cache lives in context.ScenarioInstanceData, which is per virtual user
    /// and survives across that user's iterations. That is exactly the right
    /// lifetime for a session token.
    ///
    /// Important limitation: this only works under a closed model. With
    /// Simulation.Inject each arrival is a fresh scenario instance, so
    /// ScenarioInstanceData is empty every time and the cache degenerates into
    /// the naive version without any visible sign that it has. An open-model
    /// correlation test needs a store shared across instances instead.
    /// </summary>
    public static ScenarioProps Cached(HttpClient client, string name = "auth_cached") =>
        Scenario.Create(name, async context =>
        {
            string user = TestData.PerVirtualUser(context);

            string? token = null;

            if (context.ScenarioInstanceData.TryGetValue(TokenKey, out object? existing)
                && existing is CachedToken cached
                && cached.ExpiresAt - RefreshMargin > DateTimeOffset.UtcNow)
            {
                token = cached.Token;
            }

            if (token is null)
            {
                Response<string> issued = await Step.Run("auth", context, () =>
                    IssueTokenAsync(client, user));

                if (issued.StatusCode is not "200")
                {
                    return Response.Fail<string>(statusCode: issued.StatusCode, message: "token issuance failed");
                }

                JsonNode body = JsonNode.Parse(issued.Payload.Value)!;
                token = (string)body["token"]!;

                context.ScenarioInstanceData[TokenKey] = new CachedToken(
                    token,
                    DateTimeOffset.UtcNow.AddSeconds((int)body["expiresInSeconds"]!));
            }

            await Step.Run("orders", context, () => GetOrdersAsync(client, token, user));

            await Task.Delay(ThinkTime, context.ScenarioCancellationToken);

            return Response.Ok();
        });

    private static readonly TimeSpan ThinkTime = TimeSpan.FromMilliseconds(20);

    private static async Task<Response<string>> IssueTokenAsync(HttpClient client, string user)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"/api/auth/token?user={user}");
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        return response.IsSuccessStatusCode
            ? Response.Ok(payload: body, statusCode: "200", sizeBytes: body.Length)
            : Response.Fail<string>(
                statusCode: ((int)response.StatusCode).ToString(),
                message: "POST /api/auth/token",
                sizeBytes: body.Length);
    }

    private static async Task<Response<string>> GetOrdersAsync(HttpClient client, string token, string expectedUser)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/orders/mine");
        request.Headers.Add("Authorization", $"Bearer {token}");

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            // A 401 here is almost always a defect in the test rather than the
            // service: a token cached past expiry, an unextracted value, a
            // malformed header. Naming it separately means the report says which.
            return Response.Fail<string>(
                statusCode: ((int)response.StatusCode).ToString(),
                message: response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    ? "401 - correlation broken, not a service fault"
                    : "GET /api/orders/mine",
                sizeBytes: body.Length);
        }

        // Assert the response belongs to the identity that asked for it. Without
        // this a test where every virtual user shares one token passes happily,
        // and the cache hit ratio it measures is fiction.
        string actualUser = (string)JsonNode.Parse(body)!["user"]!;

        return actualUser == expectedUser
            ? Response.Ok(payload: body, statusCode: "200", sizeBytes: body.Length)
            : Response.Fail<string>(
                statusCode: "identity-mismatch",
                message: $"expected {expectedUser}, got {actualUser}",
                sizeBytes: body.Length);
    }

    private static string ReadToken(string payload) => (string)JsonNode.Parse(payload)!["token"]!;
}
