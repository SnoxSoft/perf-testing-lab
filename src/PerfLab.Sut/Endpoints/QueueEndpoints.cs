using System.Diagnostics;
using Npgsql;
using PerfLab.Sut.Configuration;
using PerfLab.Sut.Services;

namespace PerfLab.Sut.Endpoints;

public static class QueueEndpoints
{
    public static void MapQueueEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/queue").WithTags("queue");

        // Connection pool exhaustion. Each request holds one of MaxPoolSize
        // connections for PooledHoldDuration, so the theoretical ceiling is
        // MaxPoolSize / HoldDuration requests per second — 20 / 0.05s = 400/s
        // at the defaults. Offer more than that and the excess queues:
        // throughput flattens at the ceiling while latency grows linearly.
        //
        // WaitedMs is reported separately from TotalMs so a test can attribute
        // latency to queueing rather than to work.
        group.MapGet("/pooled", async (
            NpgsqlDataSource dataSource,
            PathologyOptions options,
            CancellationToken cancellationToken) =>
        {
            long startedAt = Stopwatch.GetTimestamp();

            await using NpgsqlConnection connection =
                await dataSource.OpenConnectionAsync(cancellationToken);

            TimeSpan waited = Stopwatch.GetElapsedTime(startedAt);

            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT pg_sleep($1)";
            command.Parameters.AddWithValue(options.PooledHoldDuration.TotalSeconds);
            await command.ExecuteNonQueryAsync(cancellationToken);

            return Results.Ok(new QueueResponse(
                WaitedForConnectionMs: Math.Round(waited.TotalMilliseconds, 2),
                TotalMs: Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 2),
                PoolSize: options.MaxPoolSize));
        })
        .WithSummary("Connection pool queue. Throughput ceiling is PoolSize / HoldDuration.");

        // Lock contention. A single global gate serialises a 5 ms critical
        // section, capping throughput near 200/s no matter the core count.
        // PeakWaiters exposes the depth of the queue that produced the tail.
        group.MapPost("/reserve", async (
            InventoryLock inventory,
            CancellationToken cancellationToken) =>
        {
            long startedAt = Stopwatch.GetTimestamp();
            long reservation = await inventory.ReserveAsync(cancellationToken);

            return Results.Ok(new ReservationResponse(
                ReservationId: reservation,
                TotalMs: Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 2),
                PeakWaiters: inventory.PeakWaiters));
        })
        .WithSummary("Global lock. Serialised critical section, tail latency under contention.");

        // Slow dependency with no timeout. Under stress, InFlight climbs
        // without bound because nothing gives up — the shape of a cascading
        // failure, and the case a circuit breaker exists to prevent.
        group.MapGet("/enrich", async (SlowDependency dependency, string? subject) =>
        {
            long startedAt = Stopwatch.GetTimestamp();
            string result = await dependency.EnrichAsync(subject ?? "anonymous");

            return Results.Ok(new EnrichResponse(
                Result: result,
                TotalMs: Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 2),
                InFlight: dependency.InFlight));
        })
        .WithSummary("Downstream call with no timeout. In-flight work accumulates without bound.");

        // The same two second dependency, awaited while holding a pooled
        // connection. This is the version that actually falls over, and the
        // contrast with /enrich above is the point.
        //
        // On its own, unbounded in-flight async work is close to free in .NET: a
        // pending Task.Delay is a timer entry, so hundreds of concurrent calls
        // cost almost nothing and /enrich stays at a flat 2s under heavy load.
        // Holding a connection across the same wait changes the arithmetic
        // completely, because the scarce resource is now occupied for the whole
        // duration:
        //
        //   ceiling = pool size / dependency latency = 20 / 2s = 10 req/s
        //
        // A 400 req/s endpoint becomes a 10 req/s endpoint. Every other
        // database-backed endpoint starves too, because they share the pool —
        // which is how one slow downstream service takes down features that have
        // no dependency on it at all.
        group.MapGet("/enrich-holding", async (
            NpgsqlDataSource dataSource,
            SlowDependency dependency,
            PathologyOptions options,
            string? subject,
            CancellationToken cancellationToken) =>
        {
            long startedAt = Stopwatch.GetTimestamp();

            await using NpgsqlConnection connection =
                await dataSource.OpenConnectionAsync(cancellationToken);

            TimeSpan waited = Stopwatch.GetElapsedTime(startedAt);

            // Connection acquired and held for the entire downstream call.
            string result = await dependency.EnrichAsync(subject ?? "anonymous");

            return Results.Ok(new HoldingEnrichResponse(
                Result: result,
                WaitedForConnectionMs: Math.Round(waited.TotalMilliseconds, 2),
                TotalMs: Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 2),
                InFlight: dependency.InFlight,
                TheoreticalCeilingRps: Math.Round(
                    options.MaxPoolSize / options.SlowDependencyLatency.TotalSeconds, 2)));
        })
        .WithSummary("Slow dependency held across a pooled connection. Ceiling collapses to pool/latency.");
    }

    private sealed record QueueResponse(double WaitedForConnectionMs, double TotalMs, int PoolSize);

    private sealed record ReservationResponse(long ReservationId, double TotalMs, long PeakWaiters);

    private sealed record EnrichResponse(string Result, double TotalMs, long InFlight);

    private sealed record HoldingEnrichResponse(
        string Result,
        double WaitedForConnectionMs,
        double TotalMs,
        long InFlight,
        double TheoreticalCeilingRps);
}
