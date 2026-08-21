using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PerfLab.Bench;
using PerfLab.Results;

// perflab-bench <profile> [--repeats=3] [--tool=nbomber] [--scale=1.0] [--out=results]
//
// Runs one profile several times and reports the median with the observed range.
// A single run is enough to find a defect and not enough to publish: it cannot
// distinguish a real regression from the moment a background process woke up.

const string TargetContainer = "perflab-sut-1";
const string TargetService = "sut";
const string DefaultTarget = "http://127.0.0.1:8080";

string? Flag(string name) => args
    .FirstOrDefault(a => a.StartsWith(name, StringComparison.OrdinalIgnoreCase))
    ?[name.Length..];

string profileName = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";
int repeats = int.TryParse(Flag("--repeats="), out int r) ? r : 3;
string tool = Flag("--tool=") ?? "nbomber";
string scale = Flag("--scale=") ?? "1.0";
string outRoot = Flag("--out=") ?? "results";
string targetUrl = Environment.GetEnvironmentVariable("PERFLAB_SUT_URL") ?? DefaultTarget;

if (profileName.Length == 0)
{
    Console.Error.WriteLine("usage: perflab-bench <profile> [--repeats=3] [--tool=nbomber] [--scale=1.0]");
    return 2;
}

if (tool is not "nbomber")
{
    // k6 lands in a later phase and will emit the same schema, at which point
    // only this switch changes.
    Console.Error.WriteLine($"tool '{tool}' is not supported yet; only 'nbomber' emits the run schema.");
    return 2;
}

string suiteProject = Path.Combine("nbomber", "PerfLab.NBomber", "PerfLab.NBomber.csproj");

if (!File.Exists(suiteProject))
{
    Console.Error.WriteLine($"run this from the repository root; {suiteProject} not found.");
    return 2;
}

// Build once, up front. Rebuilding inside the run loop would put compilation on
// the clock and risk a different binary between repetitions.
Console.WriteLine("building the suite...");

if (await RunAsync("dotnet", $"build \"{suiteProject}\" -c Debug -v q --nologo") != 0)
{
    Console.Error.WriteLine("build failed.");
    return 1;
}

// Ask the suite what it knows about the profile rather than keeping a second
// copy of that knowledge here.
ProfileCatalog catalog = await ReadCatalogAsync(suiteProject);

ProfileInfo? profile = catalog.Profiles
    .FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));

if (profile is null)
{
    Console.Error.WriteLine($"unknown profile '{profileName}'. Available:");
    foreach (ProfileInfo available in catalog.Profiles)
    {
        Console.Error.WriteLine($"  {available.Name}");
    }

    return 2;
}

using HttpClient probe = new() { Timeout = TimeSpan.FromSeconds(5) };

Console.WriteLine($"profile:      {profile.Name}");
Console.WriteLine($"tool:         {tool} {catalog.ToolVersion}");
Console.WriteLine($"repetitions:  {repeats}");
Console.WriteLine($"scale:        {scale}");
Console.WriteLine($"fresh target: {(profile.RequiresFreshTarget ? "yes, restarted between runs" : "no")}");
Console.WriteLine();

string outputDirectory = Path.Combine(outRoot, tool, profile.Name);
Directory.CreateDirectory(outputDirectory);

List<RunResult> runs = [];

for (int index = 1; index <= repeats; index++)
{
    // A fresh process every repetition, always. JIT state, connection pools and
    // any static state in the suite all carry over otherwise, and the first run
    // would systematically differ from the rest.
    //
    // The target is additionally restarted for profiles whose result depends on
    // accumulated server state. A second endurance run against a process already
    // holding 90MB of leaked cache does not measure what the first one did, and
    // averaging the two describes neither.
    if (profile.RequiresFreshTarget)
    {
        Console.WriteLine($"[{index}/{repeats}] restarting the target...");

        if (await RunAsync("docker", $"compose restart {TargetService}") != 0)
        {
            Console.Error.WriteLine("could not restart the target.");
            return 1;
        }
    }

    // Pre-flight. Learned the hard way: a run against a stopped target completes
    // happily and produces a full set of transport errors that look exactly like
    // a catastrophic regression. Refusing to start is far better than recording
    // a baseline of nothing.
    if (!await IsHealthyAsync(probe, targetUrl))
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"the target at {targetUrl} is not healthy. Aborting rather than");
        Console.Error.WriteLine("recording a run full of transport errors. Try: docker compose up -d");
        return 1;
    }

    string runPath = Path.Combine(outputDirectory, $"run-{index:00}.json");

    Console.WriteLine($"[{index}/{repeats}] running...");

    int exitCode = await RunAsync(
        "dotnet",
        $"run --project \"{suiteProject}\" --no-build -- {profile.Name} " +
        $"--results=\"{Path.GetFullPath(runPath)}\" --run-index={index}",
        ("PERFLAB_SCALE", scale));

    if (!File.Exists(runPath))
    {
        Console.Error.WriteLine($"run {index} produced no result file (exit {exitCode}).");
        return 1;
    }

    RunResult run = ResultJson.Read(runPath);
    runs.Add(run);

    Console.WriteLine($"[{index}/{repeats}] {run.Outcome} in {run.DurationSeconds:N0}s");
}

// Provenance is collected after the runs, while the container that served them is
// still the one running.
string pathologies = await ReadTextAsync(probe, $"{targetUrl}/diagnostics/config");
Provenance provenance = Provenance.Collect(TargetContainer, pathologies);

BenchSummary summary = Aggregator.Summarise(runs, profile, provenance);

string summaryJson = Path.Combine(outputDirectory, "summary.json");
File.WriteAllText(summaryJson, JsonSerializer.Serialize(summary, ResultJson.Options));

string summaryMarkdown = Path.Combine(outputDirectory, "summary.md");
File.WriteAllText(summaryMarkdown, Markdown.Render(summary), Encoding.UTF8);

Console.WriteLine();
Console.WriteLine(Markdown.Render(summary));
Console.WriteLine($"written: {summaryJson}");
Console.WriteLine($"written: {summaryMarkdown}");

if (provenance.GitDirty)
{
    Console.WriteLine();
    Console.WriteLine("note: the working tree was dirty, so this result cannot be reproduced");
    Console.WriteLine("      from its own commit. Fine for exploration, not for a baseline.");
}

return 0;

static async Task<ProfileCatalog> ReadCatalogAsync(string suiteProject)
{
    using Process process = Process.Start(new ProcessStartInfo("dotnet")
    {
        Arguments = $"run --project \"{suiteProject}\" --no-build -- --list",
        RedirectStandardOutput = true,
        UseShellExecute = false,
    })!;

    string json = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();

    // The build may print ahead of the JSON, so take everything from the first
    // brace onward.
    int start = json.IndexOf('{', StringComparison.Ordinal);

    return start >= 0
        ? JsonSerializer.Deserialize<ProfileCatalog>(json[start..], ResultJson.Options)!
        : throw new InvalidOperationException("the suite did not return a profile catalog");
}

static async Task<int> RunAsync(string fileName, string arguments, params (string Key, string Value)[] environment)
{
    ProcessStartInfo startInfo = new(fileName, arguments) { UseShellExecute = false };

    foreach ((string key, string value) in environment)
    {
        startInfo.Environment[key] = value;
    }

    using Process process = Process.Start(startInfo)!;
    await process.WaitForExitAsync();
    return process.ExitCode;
}

static async Task<bool> IsHealthyAsync(HttpClient client, string targetUrl)
{
    // A short retry window, because a restarted container needs a moment and the
    // cold-start pathology delays the very first request on purpose.
    for (int attempt = 0; attempt < 30; attempt++)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync($"{targetUrl}/health");

            if (response.IsSuccessStatusCode)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Not up yet.
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    return false;
}

static async Task<string> ReadTextAsync(HttpClient client, string url)
{
    try
    {
        return await client.GetStringAsync(url);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        return string.Empty;
    }
}
