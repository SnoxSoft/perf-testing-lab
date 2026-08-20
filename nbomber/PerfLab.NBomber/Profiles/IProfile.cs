using NBomber.Contracts;

namespace PerfLab.NBomber.Profiles;

/// <summary>
/// A load shape. Scenarios say what a virtual user does; a profile says how many
/// arrive, at what rate, for how long, and what would count as a failure.
///
/// The shape *is* the test. The same scenario driven as a steady load, a ramp and
/// a spike answers three unrelated questions, and nothing about the request
/// changes between them.
/// </summary>
public interface IProfile
{
    /// <summary>Name used on the command line and as the report folder.</summary>
    string Name { get; }

    /// <summary>The question this shape answers. Printed before every run.</summary>
    string Question { get; }

    /// <summary>
    /// Whether a recorded failure should fail the run.
    ///
    /// True for shapes that assert a service level: a failure there is a
    /// regression and CI should stop. False for exploratory shapes that are
    /// designed to exceed capacity — a capacity ramp is *supposed* to end above
    /// the knee, and treating the resulting timeouts as a broken test would make
    /// the profile useless.
    /// </summary>
    bool FailOnErrors => true;

    /// <summary>
    /// Maximum managed heap growth the run may cause, in megabytes, or null when
    /// the shape does not care.
    ///
    /// Kept outside NBomber's threshold system on purpose: this is a server-side
    /// measurement the client never sees, so no expression over ScenarioStats can
    /// express it. A leak is invisible to latency thresholds until the heap is
    /// large enough to affect GC pauses, which is far too late.
    /// </summary>
    double? HeapGrowthBudgetMb => null;

    ScenarioProps[] Build(HttpClient client);
}
