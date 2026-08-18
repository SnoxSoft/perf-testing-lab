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

    ScenarioProps[] Build(HttpClient client);
}
