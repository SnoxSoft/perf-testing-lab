using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using PerfLab.NBomber;
using PerfLab.NBomber.Profiles;

// perflab-nbomber <profile> [--scenario=<name>] [nbomber args...]
IProfile[] profiles =
[
    new SmokeProfile(),
    new LoadProfile(),
    CapacityProfile.PooledQueue(),
    CapacityProfile.LockContention(),
    CapacityProfile.Ceiling(),
];

string requested = args.Length > 0 ? args[0] : "smoke";

IProfile? profile = profiles.FirstOrDefault(p =>
    string.Equals(p.Name, requested, StringComparison.OrdinalIgnoreCase));

if (profile is null)
{
    Console.Error.WriteLine($"Unknown profile '{requested}'. Available profiles:");
    foreach (IProfile available in profiles)
    {
        Console.Error.WriteLine($"  {available.Name,-10} {available.Question}");
    }

    return 2;
}

// Running a single scenario is how you get attributable numbers. Every
// database-backed scenario draws on the same connection pool, so in the full mix
// one endpoint's latency is partly caused by its neighbours — which is realistic,
// and useless when the question is "how fast is this one endpoint".
const string ScenarioFlag = "--scenario=";

string? targetScenario = args
    .FirstOrDefault(a => a.StartsWith(ScenarioFlag, StringComparison.OrdinalIgnoreCase))
    ?[ScenarioFlag.Length..];

string[] nbomberArgs = args
    .Skip(1)
    .Where(a => !a.StartsWith(ScenarioFlag, StringComparison.OrdinalIgnoreCase))
    .ToArray();

Console.WriteLine($"profile:  {profile.Name}");
Console.WriteLine($"question: {profile.Question}");
Console.WriteLine($"target:   {SutClient.BaseAddress}");

if (targetScenario is not null)
{
    Console.WriteLine($"scenario: {targetScenario} (isolated)");
}

if (RunLength.Scale is not 1.0)
{
    Console.WriteLine($"scale:    {RunLength.Scale:0.##}x — fewer samples, noisier percentiles, not a baseline");
}

Console.WriteLine();

using HttpClient client = SutClient.Create();

NBomberContext context = NBomberRunner
    .RegisterScenarios(profile.Build(client))
    .WithTestSuite("perflab")
    .WithTestName(profile.Name)
    // Reports are written per profile so two shapes never overwrite each other.
    // This folder is gitignored: generated output does not belong in history,
    // while the curated baselines under results/ do.
    .WithReportFolder($"reports/{profile.Name}")
    .WithReportFormats(ReportFormat.Html, ReportFormat.Md, ReportFormat.Csv, ReportFormat.Txt);

if (targetScenario is not null)
{
    context = context.WithTargetScenarios(targetScenario);
}

NodeStats stats = context.Run(nbomberArgs);

// A non-zero exit code is what makes this usable as a CI gate. Without it a
// breached service level objective is just text in a log that nobody reads.
ScenarioStats[] withFailures = stats.ScenarioStats
    .Where(scenario => scenario.Fail.Request.Count > 0)
    .ToArray();

if (withFailures.Length > 0)
{
    Console.Error.WriteLine();
    foreach (ScenarioStats scenario in withFailures)
    {
        Console.Error.WriteLine(
            $"{(profile.FailOnErrors ? "FAIL" : "note")} {scenario.ScenarioName}: " +
            $"{scenario.Fail.Request.Count} failed of " +
            $"{scenario.Ok.Request.Count + scenario.Fail.Request.Count}");
    }

    Console.Error.WriteLine($"See reports/{profile.Name}.");

    if (profile.FailOnErrors)
    {
        return 1;
    }

    Console.Error.WriteLine(
        "Exit code 0: this profile expects to exceed capacity, so failures above the knee are the result.");
}

return 0;
