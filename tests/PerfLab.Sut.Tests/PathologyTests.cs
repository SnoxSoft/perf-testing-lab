using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace PerfLab.Sut.Tests;

/// <summary>
/// Asserts that every pathology actually misbehaves as designed.
///
/// These are not tests of correct behaviour — they are tests that the intended
/// incorrect behaviour is still present. A defect here is silent and expensive:
/// a six hour endurance run against an endpoint whose cache quietly started
/// evicting produces a clean, flat, entirely meaningless graph. Running this
/// suite first is cheap insurance against spending an evening measuring nothing.
/// </summary>
[Collection(SharedPostgres.Name)]
public sealed class PathologyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Baseline_catalog_issues_exactly_one_query()
    {
        await using SutApplication app = SutApplication.Create(postgres);
        using HttpClient client = app.CreateClient();

        JsonNode body = await GetJsonAsync(client, "/api/catalog/products");

        Assert.Equal(1, (int)body["queryCount"]!);
        Assert.NotEmpty(body["products"]!.AsArray());
    }

    [Fact]
    public async Task NPlusOne_catalog_issues_one_query_per_row_plus_one()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("NPlusOneRowCount", "10"));
        using HttpClient client = app.CreateClient();

        JsonNode baseline = await GetJsonAsync(client, "/api/catalog/products");
        JsonNode nPlusOne = await GetJsonAsync(client, "/api/catalog/products/n-plus-one");

        // Identical result sets, wildly different query counts. If these ever
        // converge, the pathology has been optimised away by accident.
        Assert.Equal(
            baseline["products"]!.AsArray().Count,
            nPlusOne["products"]!.AsArray().Count);
        Assert.Equal(1, (int)baseline["queryCount"]!);
        Assert.Equal(11, (int)nPlusOne["queryCount"]!);
    }

    [Fact]
    public async Task Unbounded_cache_retains_every_distinct_key()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("UnboundedCache", "true"));
        using HttpClient client = app.CreateClient();

        JsonNode? last = null;
        for (int i = 0; i < 25; i++)
        {
            last = await GetJsonAsync(client, $"/api/reports/key-{i}");
        }

        Assert.Equal(25, (int)last!["cachedEntries"]!);
        Assert.True((long)last["estimatedCacheBytes"]! > 25 * 8 * 1024L * 9 / 10);
    }

    [Fact]
    public async Task Unbounded_cache_is_invisible_when_keys_repeat()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("UnboundedCache", "true"));
        using HttpClient client = app.CreateClient();

        JsonNode? last = null;
        for (int i = 0; i < 25; i++)
        {
            last = await GetJsonAsync(client, "/api/reports/always-the-same-key");
        }

        // The most common way a load test proves nothing: reusing one key means
        // measuring the cache rather than the system, and the leak never shows.
        Assert.Equal(1, (int)last!["cachedEntries"]!);
    }

    [Fact]
    public async Task Bounded_cache_retains_nothing()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("UnboundedCache", "false"));
        using HttpClient client = app.CreateClient();

        JsonNode? last = null;
        for (int i = 0; i < 25; i++)
        {
            last = await GetJsonAsync(client, $"/api/reports/key-{i}");
        }

        // The control case for the endurance comparison: same CPU cost per
        // request, no retention.
        Assert.Equal(0, (int)last!["cachedEntries"]!);
    }

    [Fact]
    public async Task Export_allocates_above_the_large_object_heap_threshold()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("ExportAllocationBytes", "131072"));
        using HttpClient client = app.CreateClient();

        JsonNode body = await GetJsonAsync(client, "/api/reports/export");

        Assert.Equal(131_072, (int)body["allocatedBytes"]!);
        Assert.True((bool)body["onLargeObjectHeap"]!);
    }

    [Fact]
    public async Task Slow_dependency_waits_at_least_the_configured_latency()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("SlowDependencyLatency", "00:00:00.400"));
        using HttpClient client = app.CreateClient();

        long startedAt = Stopwatch.GetTimestamp();
        JsonNode body = await GetJsonAsync(client, "/api/queue/enrich?subject=test");
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);

        Assert.Equal("enriched:test", (string)body["result"]!);
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(380),
            $"expected roughly 400ms of dependency latency, observed {elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task Rate_limiter_rejects_with_429_above_the_configured_rate()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("SearchRateLimitPerSecond", "5"));
        using HttpClient client = app.CreateClient();

        List<HttpStatusCode> observed = [];
        for (int i = 0; i < 20; i++)
        {
            using HttpResponseMessage response = await client.GetAsync(
                new Uri("/api/search?q=test", UriKind.Relative),
                TestContext.Current.CancellationToken);
            observed.Add(response.StatusCode);
        }

        // Five permits per second, twenty requests inside a single window.
        Assert.Equal(5, observed.Count(code => code == HttpStatusCode.OK));
        Assert.Equal(15, observed.Count(code => code == HttpStatusCode.TooManyRequests));

        // A 429 is a refusal, not a failure. Nothing here should be a 5xx, and
        // a threshold that lumps the two together would report this perfectly
        // healthy service as broken.
        Assert.DoesNotContain(observed, code => (int)code >= 500);
    }

    [Fact]
    public async Task Pooled_endpoint_queues_callers_beyond_the_pool_size()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("MaxPoolSize", "3"),
            ("PooledHoldDuration", "00:00:00.200"));
        using HttpClient client = app.CreateClient();

        // Nine callers, three connections, 200ms of hold each: the last wave
        // cannot start until the first has released, so somebody has to wait.
        JsonNode[] results = await Task.WhenAll(
            Enumerable.Range(0, 9).Select(_ => GetJsonAsync(client, "/api/queue/pooled")));

        Assert.All(results, result => Assert.Equal(3, (int)result["poolSize"]!));
        Assert.Contains(results, result => (double)result["waitedForConnectionMs"]! > 100);
    }

    [Fact]
    public async Task Cold_start_penalises_only_the_first_request()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("ColdStartPenalty", "00:00:00.500"));
        using HttpClient client = app.CreateClient();

        long firstStartedAt = Stopwatch.GetTimestamp();
        await GetJsonAsync(client, "/health");
        TimeSpan first = Stopwatch.GetElapsedTime(firstStartedAt);

        long secondStartedAt = Stopwatch.GetTimestamp();
        await GetJsonAsync(client, "/health");
        TimeSpan second = Stopwatch.GetElapsedTime(secondStartedAt);

        // This is the entire argument for a warm-up phase: without one, a single
        // outlier this large drags the mean and pollutes the high percentiles of
        // any short run.
        Assert.True(
            first >= TimeSpan.FromMilliseconds(450),
            $"first request took {first.TotalMilliseconds:F0}ms");
        Assert.True(
            second < TimeSpan.FromMilliseconds(300),
            $"second request took {second.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task Diagnostics_reports_the_configuration_that_produced_a_run()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("MaxPoolSize", "7"),
            ("SearchRateLimitPerSecond", "13"));
        using HttpClient client = app.CreateClient();

        JsonNode config = await GetJsonAsync(client, "/diagnostics/config");

        // Every committed result set records this payload. Without it, a latency
        // comparison between two runs is not a comparison.
        Assert.Equal(7, (int)config["maxPoolSize"]!);
        Assert.Equal(13, (int)config["searchRateLimitPerSecond"]!);
    }

    [Fact]
    public async Task Memory_diagnostics_expose_what_an_endurance_run_samples()
    {
        await using SutApplication app = SutApplication.Create(postgres);
        using HttpClient client = app.CreateClient();

        JsonNode snapshot = await GetJsonAsync(client, "/diagnostics/memory");

        Assert.True((long)snapshot["heapBytes"]! > 0);
        Assert.True((long)snapshot["workingSetBytes"]! > 0);
        Assert.True((int)snapshot["threadCount"]! > 0);
        Assert.NotNull(snapshot["gen2Collections"]);
        Assert.NotNull(snapshot["fragmentedBytes"]);
    }

    [Fact]
    public async Task Untimed_dependency_alone_holds_no_scarce_resource()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("MaxPoolSize", "2"),
            ("SlowDependencyLatency", "00:00:00.300"));
        using HttpClient client = app.CreateClient();

        // Four concurrent callers against a pool of two. This endpoint never
        // touches the pool, so nothing queues: a pending Task.Delay is a timer
        // entry, and concurrency costs essentially nothing.
        JsonNode[] results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ =>
                GetJsonAsync(client, "/api/queue/enrich?subject=test")));

        Assert.All(results, result =>
            Assert.True(
                (double)result["totalMs"]! < 600,
                $"expected roughly one dependency latency, observed {(double)result["totalMs"]!:F0}ms"));
    }

    [Fact]
    public async Task Holding_variant_occupies_a_connection_for_the_whole_dependency_call()
    {
        await using SutApplication app = SutApplication.Create(
            postgres,
            ("MaxPoolSize", "2"),
            ("SlowDependencyLatency", "00:00:00.300"));
        using HttpClient client = app.CreateClient();

        // Identical dependency latency, identical concurrency, but the
        // connection is held across the wait. Two connections at 300ms each
        // means the second pair of callers cannot start until the first
        // releases, so the ceiling collapses to pool size / latency.
        JsonNode[] results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ =>
                GetJsonAsync(client, "/api/queue/enrich-holding?subject=test")));

        Assert.All(results, result =>
            Assert.Equal(2 / 0.3, (double)result["theoreticalCeilingRps"]!, tolerance: 0.1));

        // Somebody waited for a connection. That is the entire difference from
        // the endpoint above, and the reason one of them collapses under load.
        Assert.Contains(results, result => (double)result["waitedForConnectionMs"]! > 200);
    }

    private static async Task<JsonNode> GetJsonAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(payload)
            ?? throw new InvalidOperationException($"{path} returned a null JSON body");
    }
}
