namespace PerfLab.NBomber.Profiles;

/// <summary>
/// Durations for the measured profiles, overridable by environment variable.
///
/// CI needs a shorter run than a local investigation, and the alternative to a
/// scale factor is a second set of hard-coded numbers that drifts out of step
/// with the first. Scaling one dial keeps the *shape* identical: a ramp with
/// half the duration still has the same number of steps in the same proportions,
/// so the knee lands in the same place.
/// </summary>
public static class RunLength
{
    /// <summary>
    /// PERFLAB_SCALE multiplies every duration. 0.25 turns a four minute profile
    /// into one minute. Values below 1 trade statistical confidence for speed:
    /// fewer samples means a noisier p99, so a scaled run is for smoke-testing
    /// the shape, not for publishing a baseline.
    /// </summary>
    public static double Scale { get; } =
        double.TryParse(Environment.GetEnvironmentVariable("PERFLAB_SCALE"), out double scale) && scale > 0
            ? scale
            : 1.0;

    public static TimeSpan Of(TimeSpan nominal) =>
        TimeSpan.FromMilliseconds(Math.Max(1_000, nominal.TotalMilliseconds * Scale));

    public static TimeSpan Seconds(double seconds) => Of(TimeSpan.FromSeconds(seconds));

    public static TimeSpan Minutes(double minutes) => Of(TimeSpan.FromMinutes(minutes));

    /// <summary>
    /// Warm-up is not scaled below a floor. Its job is to absorb JIT, connection
    /// pool fill and cache population, and those costs are fixed — they do not
    /// shrink because the measured window did. A warm-up scaled down to two
    /// seconds would leave exactly the cold-start samples it exists to exclude.
    /// </summary>
    public static TimeSpan WarmUp { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Warm-up for a scenario of the given measured duration.
    ///
    /// NBomber rejects a warm-up longer than the scenario it precedes, and warm-up
    /// here is deliberately not scaled — so at low scale factors the fixed ten
    /// seconds can exceed a shortened measured window and the run fails outright
    /// with an internal error.
    ///
    /// Clamping is the least-bad resolution, but it is not free: a shortened
    /// warm-up leaves some of the cold-start samples it exists to exclude. Hence
    /// the warning rather than silence. A scaled run is already flagged as not a
    /// baseline; this says which specific property it lost.
    /// </summary>
    public static TimeSpan WarmUpFor(TimeSpan measured)
    {
        if (WarmUp <= measured)
        {
            return WarmUp;
        }

        Console.WriteLine(
            $"warning:  warm-up clamped from {WarmUp.TotalSeconds:0.#}s to {measured.TotalSeconds:0.#}s " +
            "to fit the scaled window; cold-start samples will leak into the percentiles");

        return measured;
    }
}
