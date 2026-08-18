using PerfLab.Sut.Configuration;

namespace PerfLab.Sut.Services;

/// <summary>
/// Stands in for a downstream service that has slowed to a crawl. The call
/// accepts no cancellation token by design: without a timeout, every caller
/// waits the full latency, in-flight requests accumulate, and the failure
/// propagates outward instead of being contained. This is the mechanism behind
/// most cascading outages, and the argument for a circuit breaker.
/// </summary>
public sealed class SlowDependency(PathologyOptions options)
{
    private long _inFlight;

    public long InFlight => Interlocked.Read(ref _inFlight);

    public async Task<string> EnrichAsync(string subject)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            // Deliberately unbounded: no CancellationToken parameter exists to
            // pass, so the caller cannot give up early even if it wants to.
            await Task.Delay(options.SlowDependencyLatency);
            return $"enriched:{subject}";
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
