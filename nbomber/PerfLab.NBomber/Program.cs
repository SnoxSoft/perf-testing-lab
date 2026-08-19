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
    new SloBreachProfile(),
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
//
// Thresholds are checked before failures on purpose. Saturation shows up as a
// latency breach long before it shows up as an error, so the threshold result is
// both the earlier and the more informative signal — the pool endpoint served
// 500 req/s at 7.1 seconds latency with zero failed requests.
ThresholdResult[] breached = stats.Thresholds
    .Where(threshold => threshold.IsFailed)
    .ToArray();

// Always report how many thresholds were evaluated, including on a clean run.
// A threshold that never fires is indistinguishable from a threshold that
// cannot fire, and a gate silently reduced to zero checks still exits 0.
Console.WriteLine();
Console.WriteLine(
    $"service level objectives: {stats.Thresholds.Length - breached.Length} passed, " +
    $"{breached.Length} breached");

if (breached.Length > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{breached.Length} service level objective(s) breached:");

    foreach (ThresholdResult threshold in breached)
    {
        string scope = string.IsNullOrWhiteSpace(threshold.StepName)
            ? threshold.ScenarioName
            : $"{threshold.ScenarioName}/{threshold.StepName}";

        Console.Error.WriteLine($"  BREACH {scope}: {threshold.CheckExpression}");

        if (!string.IsNullOrWhiteSpace(threshold.ExceptionMsg))
        {
            Console.Error.WriteLine($"         {threshold.ExceptionMsg}");
        }
    }

    Console.Error.WriteLine($"See reports/{profile.Name}.");
    return 1;
}

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
