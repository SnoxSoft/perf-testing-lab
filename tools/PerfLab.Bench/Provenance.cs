using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PerfLab.Bench;

/// <summary>
/// Everything needed to interpret a set of numbers later, or to know that you
/// cannot.
///
/// Collected by the harness rather than the suite because the harness is what
/// knows about git and Docker. A tool under test should not be shelling out to
/// either.
///
/// Nothing user-identifying is recorded. These files are intended to be
/// committed to a public repository, so the machine name, user name and absolute
/// paths are deliberately absent — the useful facts are the core count and the
/// limits applied to the target, not whose laptop it was.
/// </summary>
public sealed record Provenance
{
    public required string CollectedAtUtc { get; init; }

    public required string GitSha { get; init; }

    /// <summary>
    /// True when the working tree had uncommitted changes. A baseline measured
    /// from a dirty tree cannot be reproduced from its own commit, which makes it
    /// evidence of nothing.
    /// </summary>
    public bool GitDirty { get; init; }

    public required HostInfo Host { get; init; }

    public required TargetInfo Target { get; init; }

    public static Provenance Collect(string composeService, string diagnosticsConfigJson)
    {
        string sha = Git("rev-parse HEAD").Trim();
        bool dirty = Git("status --porcelain").Trim().Length > 0;

        return new Provenance
        {
            CollectedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            GitSha = string.IsNullOrWhiteSpace(sha) ? "unknown" : sha,
            GitDirty = dirty,
            Host = new HostInfo
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                DotnetVersion = Environment.Version.ToString(),
            },
            Target = new TargetInfo
            {
                ImageId = Docker($"inspect --format {{{{.Image}}}} {composeService}").Trim(),
                CpuLimit = Docker($"inspect --format {{{{.HostConfig.NanoCpus}}}} {composeService}").Trim(),
                MemoryLimitBytes = Docker($"inspect --format {{{{.HostConfig.Memory}}}} {composeService}").Trim(),
                Pathologies = diagnosticsConfigJson,
            },
        };
    }

    private static string Git(string arguments) => Capture("git", arguments);

    private static string Docker(string arguments) => Capture("docker", arguments);

    private static string Capture(string fileName, string arguments)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return string.Empty;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Absent git or docker is not a reason to abandon a run; it is a
            // reason for the baseline to say so.
            return string.Empty;
        }
    }
}

public sealed record HostInfo
{
    public required string OperatingSystem { get; init; }

    public required string Architecture { get; init; }

    public int ProcessorCount { get; init; }

    public required string DotnetVersion { get; init; }
}

public sealed record TargetInfo
{
    public required string ImageId { get; init; }

    /// <summary>Nanocpus as reported by Docker; 2000000000 is two cores.</summary>
    public required string CpuLimit { get; init; }

    public required string MemoryLimitBytes { get; init; }

    /// <summary>
    /// The target's own /diagnostics/config payload, verbatim.
    ///
    /// This is the single most important field. A latency comparison between two
    /// runs configured differently is not a comparison, and without this there is
    /// no way to know afterwards which is which.
    /// </summary>
    public required string Pathologies { get; init; }
}
