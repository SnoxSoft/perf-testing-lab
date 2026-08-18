using PerfLab.Sut.Configuration;
using PerfLab.Sut.Services;

namespace PerfLab.Sut.Endpoints;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/reports").WithTags("reports");

        // The endurance target. Each distinct key retains ~8 KB forever, so a
        // test that requests unique keys grows the heap for as long as it runs
        // while a test that reuses keys does not. Feeding this endpoint the same
        // key every iteration is the single most common way a load test proves
        // nothing: it measures the cache, not the system.
        group.MapGet("/{key}", (
            string key,
            UnboundedReportCache cache) =>
        {
            byte[] report = cache.GetOrCreate(key);

            return Results.Ok(new ReportResponse(
                Key: key,
                SizeBytes: report.Length,
                CachedEntries: cache.Count,
                EstimatedCacheBytes: cache.EstimatedBytes));
        })
        .WithSummary("Unbounded cache. Unique keys leak; repeated keys hide the leak.");

        // Large object heap pressure. Buffers above 85,000 bytes bypass the
        // generational heap and land on the LOH, which is not compacted by
        // default — so sustained load fragments it rather than merely filling
        // it. Gen2 collections and pause times are the signal, not raw RSS.
        group.MapGet("/export", (PathologyOptions options) =>
        {
            byte[] buffer = new byte[options.ExportAllocationBytes];
            buffer[0] = 1;
            buffer[^1] = 1;

            return Results.Ok(new ExportResponse(
                AllocatedBytes: buffer.Length,
                OnLargeObjectHeap: buffer.Length >= 85_000,
                Gen2Collections: GC.CollectionCount(2)));
        })
        .WithSummary("Large object heap allocation per request. Fragmentation, not just volume.");
    }

    private sealed record ReportResponse(string Key, int SizeBytes, int CachedEntries, long EstimatedCacheBytes);

    private sealed record ExportResponse(int AllocatedBytes, bool OnLargeObjectHeap, int Gen2Collections);
}
