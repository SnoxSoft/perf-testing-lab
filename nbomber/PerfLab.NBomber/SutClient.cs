using System.Net;

namespace PerfLab.NBomber;

/// <summary>
/// The HTTP client every scenario shares.
///
/// A load generator's own connection handling is the first thing to get right,
/// because when it is wrong the results look like a slow server rather than a
/// slow client. Two settings matter more than the rest, and both are set
/// explicitly here rather than left to a default that might change.
/// </summary>
public static class SutClient
{
    /// <summary>
    /// Note the literal 127.0.0.1. Using "localhost" here cost a factor of 4575
    /// in measured throughput, and the mistake is invisible in the results —
    /// it looks like a slow server.
    ///
    /// On Windows, "localhost" resolves to ::1 first. Docker Desktop publishes
    /// the container port on IPv4 only, so there is nothing listening on IPv6.
    /// .NET attempts ::1, waits out the platform TCP connect timeout of roughly
    /// 21 seconds, then falls back to IPv4. Measured against this SUT:
    ///
    ///   localhost   1 request in 5s,    p50 = 21184ms
    ///   127.0.0.1   4575 requests in 5s, p50 = 0.88ms
    ///
    /// The 21 second samples then land in the high percentiles and the report
    /// reads as a catastrophically slow service. Always resolve the target
    /// explicitly in a load test, and be suspicious of any latency that looks
    /// like a round platform timeout rather than a property of the system.
    /// </summary>
    public static Uri BaseAddress { get; } = new(
        Environment.GetEnvironmentVariable("PERFLAB_SUT_URL") ?? "http://127.0.0.1:8080");

    public static HttpClient Create()
    {
        SocketsHttpHandler handler = new()
        {
            // Fail fast rather than absorbing a platform default measured in
            // tens of seconds. If the target is unreachable the run should say
            // so immediately, not quietly contribute connect timeouts to p99.
            ConnectTimeout = TimeSpan.FromSeconds(5),

            // Unlimited. The default is already effectively unbounded on .NET,
            // but stating it means a future default change cannot silently turn
            // this generator into the bottleneck. Cap it deliberately only when
            // modelling a client that genuinely has a connection ceiling.
            MaxConnectionsPerServer = int.MaxValue,

            // Long-lived connections, recycled on a fixed schedule. Without a
            // lifetime, a run never re-resolves DNS; without pooling, every
            // iteration pays a TCP and TLS handshake and the test measures
            // connection setup instead of the endpoint.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),

            // Expect no redirects and no cookies. Both add hidden work per
            // request, and a redirect silently doubles the request count.
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };

        return new HttpClient(handler)
        {
            BaseAddress = BaseAddress,

            // Deliberately longer than the slowest pathology. A client timeout
            // shorter than the server's worst case turns a latency measurement
            // into a client-side error and hides the very behaviour under test.
            Timeout = TimeSpan.FromSeconds(60),
        };
    }
}
