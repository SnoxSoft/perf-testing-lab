using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace PerfLab.Sut.Tests;

/// <summary>
/// Boots the real application against the throwaway database, with pathology
/// settings overridden per test. Each test that cares about a pathology states
/// the configuration it depends on, so a default changing in appsettings.json
/// cannot silently invalidate an assertion.
/// </summary>
public sealed class SutApplication(string connectionString, IDictionary<string, string?> overrides)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);

        Dictionary<string, string?> settings = new(overrides)
        {
            ["ConnectionStrings:Sut"] = connectionString,
        };

        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(settings));
    }

    public static SutApplication Create(
        PostgresFixture postgres,
        params (string Key, string Value)[] pathologyOverrides)
    {
        Dictionary<string, string?> overrides = new()
        {
            // Off unless a test explicitly asks for it. A one second delay on
            // the first request is the point of the pathology and pure noise
            // in a correctness assertion.
            ["Pathologies:ColdStartPenalty"] = "00:00:00",
        };

        foreach ((string key, string value) in pathologyOverrides)
        {
            overrides[$"Pathologies:{key}"] = value;
        }

        return new SutApplication(postgres.ConnectionString, overrides);
    }
}
