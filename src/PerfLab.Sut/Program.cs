using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using PerfLab.Sut.Configuration;
using PerfLab.Sut.Data;
using PerfLab.Sut.Endpoints;
using PerfLab.Sut.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Everything below resolves PathologyOptions from DI rather than reading
// configuration here at startup. Binding eagerly would be shorter, but it fixes
// the values before any late configuration source can be added — which silently
// breaks the correctness tests, since WebApplicationFactory contributes its
// overrides during Build(). A pathology that cannot be reconfigured per run is
// not much of a laboratory.
builder.Services
    .AddOptions<PathologyOptions>()
    .Bind(builder.Configuration.GetSection(PathologyOptions.SectionName));

builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<IOptions<PathologyOptions>>().Value);

// The pool ceiling is the point, so it is applied here rather than left to the
// connection string: a reader should not have to notice a query parameter to
// understand why throughput plateaus where it does.
builder.Services.AddSingleton(serviceProvider =>
{
    PathologyOptions pathologies = serviceProvider.GetRequiredService<PathologyOptions>();
    IConfiguration configuration = serviceProvider.GetRequiredService<IConfiguration>();

    string connectionString = configuration.GetConnectionString("Sut")
        ?? "Host=localhost;Port=5432;Database=perflab;Username=perflab;Password=perflab";

    NpgsqlConnectionStringBuilder connectionBuilder = new(connectionString)
    {
        MaxPoolSize = pathologies.MaxPoolSize,
        Timeout = 30,
    };

    return NpgsqlDataSource.Create(connectionBuilder.ConnectionString);
});

builder.Services.AddSingleton<UnboundedReportCache>();
builder.Services.AddSingleton<InventoryLock>();
builder.Services.AddSingleton<SlowDependency>();
builder.Services.AddSingleton<TokenIssuer>();
builder.Services.AddHostedService<DatabaseInitializer>();

// A real rate limiter rather than a simulated one, so the 429 a spike test sees
// carries the same headers and semantics it would in production. The partition
// key is constant, so every caller shares one window: this models a global
// service limit, not a per-client quota.
//
// QueueLimit is zero. Excess requests are rejected immediately instead of being
// parked, which keeps rejection cleanly distinguishable from latency — queueing
// them would convert a visible 429 into an invisible slowdown.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(SearchEndpoints.RateLimitPolicy, httpContext =>
    {
        PathologyOptions pathologies =
            httpContext.RequestServices.GetRequiredService<PathologyOptions>();

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: "global",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = pathologies.SearchRateLimitPerSecond,
                Window = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
    });
});

WebApplication app = builder.Build();

app.UseRateLimiter();

// Cold start, applied to the first request only. NBomber has a first-class
// warm-up phase that discards these samples; k6 requires arranging the
// equivalent by hand. Leaving a real penalty here makes the consequence of
// skipping warm-up visible in the percentiles instead of hypothetical.
int coldStartServed = 0;
app.Use(async (context, next) =>
{
    TimeSpan penalty = context.RequestServices
        .GetRequiredService<PathologyOptions>()
        .ColdStartPenalty;

    if (penalty > TimeSpan.Zero && Interlocked.Exchange(ref coldStartServed, 1) == 0)
    {
        await Task.Delay(penalty, context.RequestAborted);
    }

    await next(context);
});

app.MapDiagnosticsEndpoints();
app.MapCatalogEndpoints();
app.MapQueueEndpoints();
app.MapReportEndpoints();
app.MapSearchEndpoints();
app.MapAuthEndpoints();

await app.RunAsync();
