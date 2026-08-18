using System.Collections.Concurrent;
using PerfLab.Sut.Configuration;

namespace PerfLab.Sut.Services;

/// <summary>
/// A cache that never evicts, which is the most common shape of a real .NET
/// memory leak: a correct-looking dictionary keyed by something unbounded.
///
/// This is what makes an endurance test worth running. The pathology is
/// invisible in a five-minute load test and unmistakable after two hours,
/// which is precisely the class of defect that only duration exposes.
/// </summary>
public sealed class UnboundedReportCache(PathologyOptions options)
{
    private readonly ConcurrentDictionary<string, byte[]> _entries = new();

    public int Count => _entries.Count;

    public long EstimatedBytes => _entries.Sum(entry => (long)entry.Value.Length + entry.Key.Length);

    public byte[] GetOrCreate(string key)
    {
        if (!options.UnboundedCache)
        {
            // Bounded mode: compute and discard. Same CPU cost per request,
            // no retention — the control case for the endurance comparison.
            return Render(key);
        }

        return _entries.GetOrAdd(key, static k => Render(k));
    }

    private static byte[] Render(string key)
    {
        // ~8 KB per entry. Small enough to look harmless in a code review,
        // large enough that a few hundred thousand distinct keys matter.
        byte[] payload = new byte[8 * 1024];
        System.Text.Encoding.UTF8.GetBytes(key, payload);
        return payload;
    }
}
