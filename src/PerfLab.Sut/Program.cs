using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using PerfLab.Sut.Configuration;
using PerfLab.Sut.Data;
using PerfLab.Sut.Endpoints;
using PerfLab.Sut.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

PathologyOptions pathologies =
    builder.Configuration.GetSection(PathologyOptions.SectionName).Get<PathologyOptions>()
    ?? new PathologyOptions();

builder.Services.AddSingleton(pathologies);

// The pool ceiling is the point, so it is set here rather than left to the
// connection string: a reader should not have to notice a query parameter to
// understand why throughput plateaus where it does.
string connectionString = builder.Configuration.GetConnectionString("Sut")
    ?? "Host=localhost;Port=5432;Database=perflab;Username=perflab;Password=perflab";

NpgsqlConnectionStringBuilder connectionBuilder = new(connectionString)
{
    MaxPoolSize = pathologies.MaxPoolSize,
    Timeout = 30,
};

builder.Services.AddNpgsqlDataSource(connectionBuilder.ConnectionString);

builder.Services.AddSingleton<UnboundedReportCache>();
builder.Services.AddSingleton<InventoryLock>();
builder.Services.AddSingleton<SlowDependency>();
builder.Services.AddHostedService<DatabaseInitializer>();

// A real rate limiter rather than a simulated one, so the 429 a spike test sees
// carries the same headers and semantics it would in production. QueueLimit is
// zero: excess requests are rejected immediately instead of being parked, which
// keeps rejection distinguishable from latency.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddFixedWindowLimiter(SearchEndpoints.RateLimitPolicy, window =>
    {
        window.PermitLimit = pathologies.SearchRateLimitPerSecond;
        window.Window = TimeSpan.FromSeconds(1);
        window.QueueLimit = 0;
        window.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

WebApplication app = builder.Build();

app.UseRateLimiter();

// Cold start, applied to the first request only. NBomber has a first-class
// warm-up phase that discards these samples; k6 requires arranging the
// equivalent by hand. Leaving a real penalty here makes the consequence of
// skipping warm-up visible in the percentiles instead of hypothetical.
if (pathologies.ColdStartPenalty > TimeSpan.Zero)
{
    int coldStartServed = 0;
    app.Use(async (context, next) =>
    {
        if (Interlocked.Exchange(ref coldStartServed, 1) == 0)
        {
            await Task.Delay(pathologies.ColdStartPenalty, context.RequestAborted);
        }

        await next(context);
    });
}

app.MapDiagnosticsEndpoints();
app.MapCatalogEndpoints();
app.MapQueueEndpoints();
app.MapReportEndpoints();
app.MapSearchEndpoints();

await app.RunAsync();
