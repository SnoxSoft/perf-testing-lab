using Testcontainers.PostgreSql;

namespace PerfLab.Sut.Tests;

/// <summary>
/// A throwaway PostgreSQL instance, started once for the whole test run.
///
/// Testcontainers is the right tool here and the wrong tool for the measured
/// runs. These tests assert behaviour, so a cold, freshly started, randomly
/// ported database is fine — ideal, even, because it guarantees isolation. The
/// load tests assert timing, which needs a warmed, resource-pinned, long-lived
/// target, so they run against the Compose environment instead. The distinction
/// is explained in docs/methodology.md.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("perflab")
        .WithUsername("perflab")
        .WithPassword("perflab")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => new(_container.DisposeAsync().AsTask());
}

[CollectionDefinition(Name)]
public sealed class SharedPostgres : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
