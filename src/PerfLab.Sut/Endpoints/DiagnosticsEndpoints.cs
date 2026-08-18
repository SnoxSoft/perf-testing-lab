using System.Diagnostics;
using PerfLab.Sut.Configuration;
using PerfLab.Sut.Services;

namespace PerfLab.Sut.Endpoints;

public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
            .WithTags("diagnostics")
            .WithSummary("Liveness. No database, no allocation, no locks.");

        // Framework overhead floor. If a load test cannot push this endpoint
        // far beyond the throughput of any other route, the bottleneck is the
        // load generator or the network — not the service. Establishing that
        // ceiling first is what separates a real finding from a measurement
        // artefact.
        app.MapGet("/api/echo", () => Results.Ok(new { ok = true }))
            .WithTags("diagnostics")
            .WithSummary("Minimal handler. Use it to prove the generator is not the bottleneck.");

        RouteGroupBuilder group = app.MapGroup("/diagnostics").WithTags("diagnostics");

        // Polled by the endurance tests. Memory growth is the whole point of a
        // soak run, and sampling it from inside the process avoids depending on
        // container stats or an external profiler.
        //
        // HeapBytes and Gen2Collections matter more than WorkingSetBytes: the
        // OS is free to leave freed pages mapped, so RSS can plateau while the
        // managed heap keeps growing.
        group.MapGet("/memory", (
            UnboundedReportCache cache,
            SlowDependency dependency,
            InventoryLock inventory) =>
        {
            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            using Process process = Process.GetCurrentProcess();

            return Results.Ok(new MemorySnapshot(
                HeapBytes: GC.GetTotalMemory(forceFullCollection: false),
                WorkingSetBytes: process.WorkingSet64,
                FragmentedBytes: gcInfo.FragmentedBytes,
                Gen0Collections: GC.CollectionCount(0),
                Gen1Collections: GC.CollectionCount(1),
                Gen2Collections: GC.CollectionCount(2),
                PauseTimePercentage: gcInfo.PauseTimePercentage,
                ThreadCount: process.Threads.Count,
                CachedReportEntries: cache.Count,
                EstimatedCacheBytes: cache.EstimatedBytes,
                DependencyCallsInFlight: dependency.InFlight,
                PeakLockWaiters: inventory.PeakWaiters,
                UptimeSeconds: Math.Round((DateTimeOffset.UtcNow - StartedAt).TotalSeconds, 1)));
        })
        .WithSummary("Heap, GC and queue depth. Sampled by the endurance tests.");

        // Committed alongside every result set, so a baseline records the
        // configuration that produced it. A latency comparison between two runs
        // with different pathology settings is not a comparison.
        group.MapGet("/config", (PathologyOptions options) => Results.Ok(options))
            .WithSummary("Active pathology configuration. Recorded with every result set.");
    }

    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    private sealed record MemorySnapshot(
        long HeapBytes,
        long WorkingSetBytes,
        long FragmentedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        double PauseTimePercentage,
        int ThreadCount,
        int CachedReportEntries,
        long EstimatedCacheBytes,
        long DependencyCallsInFlight,
        long PeakLockWaiters,
        double UptimeSeconds);
}
