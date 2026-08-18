using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;
using PerfLab.NBomber;
using PerfLab.NBomber.Profiles;

// Load shapes are selected by name: perflab-nbomber <profile>. Anything after
// the profile name is handed to NBomber itself, so its own switches still work.
IProfile[] profiles = [new SmokeProfile()];

string requested = args.Length > 0 ? args[0] : "smoke";

IProfile? profile = profiles.FirstOrDefault(p =>
    string.Equals(p.Name, requested, StringComparison.OrdinalIgnoreCase));

if (profile is null)
{
    Console.Error.WriteLine($"Unknown profile '{requested}'. Available profiles:");
    foreach (IProfile available in profiles)
    {
        Console.Error.WriteLine($"  {available.Name,-12} {available.Question}");
    }

    return 2;
}

Console.WriteLine($"profile:  {profile.Name}");
Console.WriteLine($"question: {profile.Question}");
Console.WriteLine($"target:   {SutClient.BaseAddress}");
Console.WriteLine();

using HttpClient client = SutClient.Create();

NodeStats stats = NBomberRunner
    .RegisterScenarios(profile.Build(client))
    .WithTestSuite("perflab")
    .WithTestName(profile.Name)
    // Reports are written per profile so two shapes never overwrite each other.
    // This folder is gitignored: generated output does not belong in history,
    // while the curated baselines under results/ do.
    .WithReportFolder($"reports/{profile.Name}")
    .WithReportFormats(ReportFormat.Html, ReportFormat.Md, ReportFormat.Csv, ReportFormat.Txt)
    .Run(args.Skip(1).ToArray());

// A non-zero exit code is what makes this usable as a CI gate. Without it a
// breached service level objective is just text in a log that nobody reads.
int failed = stats.ScenarioStats.Count(scenario => scenario.Fail.Request.Count > 0);

if (failed > 0)
{
    Console.Error.WriteLine($"{failed} scenario(s) recorded failures. See reports/{profile.Name}.");
    return 1;
}

return 0;
