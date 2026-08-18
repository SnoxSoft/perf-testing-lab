namespace PerfLab.Sut.Services;

/// <summary>
/// A single global lock guarding a hot path. Throughput past saturation is
/// capped at 1 / criticalSectionDuration regardless of how many cores or
/// callers exist, and the queue in front of it is where tail latency comes
/// from — p50 can stay respectable while p99 falls apart.
/// </summary>
public sealed class InventoryLock : IDisposable
{
    private static readonly TimeSpan CriticalSection = TimeSpan.FromMilliseconds(5);

    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private long _reservations;
    private long _peakWaiters;
    private long _currentWaiters;

    public long Reservations => Interlocked.Read(ref _reservations);

    public long PeakWaiters => Interlocked.Read(ref _peakWaiters);

    public async Task<long> ReserveAsync(CancellationToken cancellationToken)
    {
        long waiting = Interlocked.Increment(ref _currentWaiters);
        RecordPeak(waiting);

        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _currentWaiters);
        }

        try
        {
            // Serialised work. Everything else is queued behind this.
            await Task.Delay(CriticalSection, cancellationToken);
            return Interlocked.Increment(ref _reservations);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RecordPeak(long waiting)
    {
        long observed = Interlocked.Read(ref _peakWaiters);
        while (waiting > observed)
        {
            long previous = Interlocked.CompareExchange(ref _peakWaiters, waiting, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    public void Dispose() => _gate.Dispose();
}
