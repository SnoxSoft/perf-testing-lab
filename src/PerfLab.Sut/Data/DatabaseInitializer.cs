using Npgsql;

namespace PerfLab.Sut.Data;

/// <summary>
/// Creates and seeds the schema at startup, retrying while PostgreSQL finishes
/// accepting connections. Seeding happens once and the row counts are fixed, so
/// a query's cost is a property of the code rather than of how many times the
/// suite has been run.
/// </summary>
public sealed partial class DatabaseInitializer(
    NpgsqlDataSource dataSource,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    private const int CategoryCount = 12;
    private const int ProductCount = 5_000;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await WaitForDatabaseAsync(cancellationToken);

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await using (NpgsqlCommand schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS categories (
                    id   int PRIMARY KEY,
                    name text NOT NULL
                );

                CREATE TABLE IF NOT EXISTS products (
                    id          int PRIMARY KEY,
                    name        text NOT NULL,
                    category_id int  NOT NULL REFERENCES categories (id),
                    price       numeric(10, 2) NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_products_category ON products (category_id);
                """;
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (NpgsqlCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT count(*) FROM products";
            long existing = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
            if (existing == ProductCount)
            {
                Log.SchemaAlreadySeeded(logger, existing);
                return;
            }
        }

        await using (NpgsqlCommand seed = connection.CreateCommand())
        {
            seed.CommandText = $"""
                TRUNCATE products, categories;

                INSERT INTO categories (id, name)
                SELECT i, 'category-' || i FROM generate_series(1, {CategoryCount}) AS i;

                INSERT INTO products (id, name, category_id, price)
                SELECT i,
                       'product-' || i,
                       (i % {CategoryCount}) + 1,
                       (random() * 500)::numeric(10, 2)
                FROM generate_series(1, {ProductCount}) AS i;

                ANALYZE products;
                ANALYZE categories;
                """;
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        Log.Seeded(logger, ProductCount, CategoryCount);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WaitForDatabaseAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 30;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using NpgsqlConnection probe = await dataSource.OpenConnectionAsync(cancellationToken);
                return;
            }
            catch (NpgsqlException) when (attempt < maxAttempts)
            {
                Log.WaitingForDatabase(logger, attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Source-generated log delegates. Startup logging is not on a hot path, but
    /// the analyser rules are enabled repository-wide precisely so that logging
    /// on a hot path cannot be written carelessly out of habit.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Schema already seeded with {RowCount} products")]
        public static partial void SchemaAlreadySeeded(ILogger logger, long rowCount);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Seeded {ProductCount} products across {CategoryCount} categories")]
        public static partial void Seeded(ILogger logger, int productCount, int categoryCount);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Waiting for PostgreSQL (attempt {Attempt}/{MaxAttempts})")]
        public static partial void WaitingForDatabase(ILogger logger, int attempt, int maxAttempts);
    }
}
