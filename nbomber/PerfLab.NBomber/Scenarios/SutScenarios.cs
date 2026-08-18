using System.Net;
using NBomber.Contracts;
using NBomber.CSharp;

namespace PerfLab.NBomber.Scenarios;

/// <summary>
/// One scenario per pathology, each with its load simulations left unset.
///
/// The separation matters: a scenario describes *what* a virtual user does, a
/// profile describes *how many* arrive and *when*. Keeping them apart is what
/// lets the same request pattern be driven as a smoke test, a capacity ramp and
/// a six hour soak without being rewritten three times — and it makes the load
/// shape the only variable between two runs.
/// </summary>
public static class SutScenarios
{
    /// <summary>
    /// The reference point: one indexed join. Every other latency figure in the
    /// suite is only meaningful relative to this one.
    /// </summary>
    public static ScenarioProps Baseline(HttpClient client, string name = "baseline") =>
        Scenario.Create(name, async context =>
        {
            Response<string> response = await Step.Run("query", context, () =>
                SendAsync(client, HttpMethod.Get, "/api/catalog/products"));

            return response;
        });

    /// <summary>
    /// Framework overhead floor. Run this before believing any other number: if
    /// it cannot reach many multiples of the throughput of the endpoint under
    /// test, the generator is the bottleneck and the result is an artefact.
    /// </summary>
    public static ScenarioProps GeneratorCeiling(HttpClient client, string name = "generator_ceiling") =>
        Scenario.Create(name, async context =>
            await SendAsync(client, HttpMethod.Get, "/api/echo"));

    /// <summary>
    /// N+1 against the baseline. Two steps in one iteration, so NBomber reports
    /// their latencies separately and the difference is attributable rather than
    /// inferred.
    /// </summary>
    public static ScenarioProps NPlusOneComparison(HttpClient client) =>
        Scenario.Create("n_plus_one_comparison", async context =>
        {
            await Step.Run("one_query", context, () =>
                SendAsync(client, HttpMethod.Get, "/api/catalog/products"));

            await Step.Run("n_queries", context, () =>
                SendAsync(client, HttpMethod.Get, "/api/catalog/products/n-plus-one"));

            return Response.Ok();
        });

    /// <summary>
    /// The connection pool queue. Throughput ceiling is pool size divided by
    /// hold time — 20 / 0.05s = 400 req/s at the defaults. Drive this past the
    /// ceiling and latency grows linearly while throughput stays flat.
    /// </summary>
    public static ScenarioProps PooledQueue(HttpClient client, string name = "pooled_queue") =>
        Scenario.Create(name, async context =>
            await SendAsync(client, HttpMethod.Get, "/api/queue/pooled"));

    /// <summary>
    /// Global lock. Serialised 5ms critical section caps throughput near 200/s
    /// regardless of core count; the interesting number is p99, not p50.
    /// </summary>
    public static ScenarioProps LockContention(HttpClient client, string name = "lock_contention") =>
        Scenario.Create(name, async context =>
            await SendAsync(client, HttpMethod.Post, "/api/queue/reserve"));

    /// <summary>
    /// Downstream call with no timeout. Under an open-model simulation the
    /// server's in-flight count climbs without bound, which is the mechanism
    /// behind a cascading failure. Under a closed model it will not, and that
    /// difference is the whole argument for choosing the model deliberately.
    /// </summary>
    public static ScenarioProps SlowDependency(HttpClient client) =>
        Scenario.Create("slow_dependency", async context =>
            await SendAsync(client, HttpMethod.Get, "/api/queue/enrich?subject=perflab"));

    /// <summary>
    /// The endurance target. Each iteration requests a key that has never been
    /// requested before, so the server retains ~8KB per iteration for as long as
    /// the run lasts.
    ///
    /// InvocationNumber is what makes the key unique. Reusing one key is the
    /// single most common way a load test proves nothing: it measures the cache
    /// and the leak never appears.
    /// </summary>
    public static ScenarioProps UniqueReports(HttpClient client) =>
        Scenario.Create("unique_reports", async context =>
            await SendAsync(
                client,
                HttpMethod.Get,
                $"/api/reports/{context.ScenarioInfo.InstanceId}-{context.InvocationNumber}"));

    /// <summary>
    /// The control case for the endurance comparison. Identical shape, one key.
    /// Run this alongside <see cref="UniqueReports"/> and the flat memory graph
    /// is the point being made.
    /// </summary>
    public static ScenarioProps RepeatedReports(HttpClient client) =>
        Scenario.Create("repeated_reports", async context =>
            await SendAsync(client, HttpMethod.Get, "/api/reports/always-the-same-key"));

    /// <summary>
    /// Large object heap pressure. Watch Gen2 collections and pause time rather
    /// than resident memory: the LOH is not compacted by default, so the defect
    /// is fragmentation and RSS can plateau while it worsens.
    /// </summary>
    public static ScenarioProps AllocationPressure(HttpClient client) =>
        Scenario.Create("allocation_pressure", async context =>
            await SendAsync(client, HttpMethod.Get, "/api/reports/export"));

    /// <summary>
    /// The rate limited endpoint, where 429 is recorded as a successful outcome.
    ///
    /// This is the most important judgement call in the suite. A 429 means the
    /// service refused work it could not safely accept — correct behaviour, and
    /// categorically different from a 500 or a timeout. Marking it as a failure
    /// would make every spike test report a healthy service as broken. The
    /// status code is still attached, so the split stays visible in the report
    /// and a threshold can budget for it explicitly.
    /// </summary>
    public static ScenarioProps RateLimitedSearch(HttpClient client) =>
        Scenario.Create("rate_limited_search", async context =>
            await SendAsync(
                client,
                HttpMethod.Get,
                "/api/search?q=perflab",
                treatAsOk: static status => status is HttpStatusCode.OK or HttpStatusCode.TooManyRequests));

    /// <summary>
    /// Issues one request and translates the result into an NBomber response.
    ///
    /// Three details worth noting, because each is a way a load test can quietly
    /// measure the wrong thing:
    ///
    /// 1. The body is fully read. Abandoning it leaves the response streaming in
    ///    the background, so latency is recorded as time-to-headers and the run
    ///    reports a service faster than it is.
    /// 2. The status code is always attached, so the report shows the actual
    ///    distribution of outcomes instead of a single pass/fail ratio.
    /// 3. Size is recorded, which is what makes generator network saturation
    ///    visible when it eventually becomes the constraint.
    /// </summary>
    private static async Task<Response<string>> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        Func<HttpStatusCode, bool>? treatAsOk = null)
    {
        using HttpRequestMessage request = new(method, path);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            string statusCode = ((int)response.StatusCode).ToString();

            bool isOk = treatAsOk?.Invoke(response.StatusCode) ?? response.IsSuccessStatusCode;

            return isOk
                ? Response.Ok(payload: body, statusCode: statusCode, sizeBytes: body.Length)
                : Response.Fail<string>(statusCode: statusCode, message: $"{method} {path}", sizeBytes: body.Length);
        }
        catch (TaskCanceledException)
        {
            // A client-side timeout, reported distinctly from a server error.
            // Conflating the two hides whether the service refused, failed or
            // simply took longer than the client was willing to wait.
            return Response.Fail<string>(statusCode: "timeout", message: $"{method} {path} exceeded the client timeout");
        }
        catch (HttpRequestException exception)
        {
            return Response.Fail<string>(statusCode: "transport", message: exception.Message);
        }
    }
}
