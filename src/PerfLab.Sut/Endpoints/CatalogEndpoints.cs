using Npgsql;
using PerfLab.Sut.Configuration;

namespace PerfLab.Sut.Endpoints;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/catalog").WithTags("catalog");

        // The control case. One round trip, indexed join, fixed row count.
        // Every other latency number in this repository is only meaningful
        // relative to this one.
        group.MapGet("/products", async (
            NpgsqlDataSource dataSource,
            PathologyOptions options,
            CancellationToken cancellationToken) =>
        {
            await using NpgsqlConnection connection =
                await dataSource.OpenConnectionAsync(cancellationToken);
            await using NpgsqlCommand command = connection.CreateCommand();

            command.CommandText = """
                SELECT p.id, p.name, c.name AS category, p.price
                FROM products p
                JOIN categories c ON c.id = p.category_id
                ORDER BY p.id
                LIMIT $1
                """;
            command.Parameters.AddWithValue(options.NPlusOneRowCount);

            List<ProductView> products = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                products.Add(new ProductView(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDecimal(3)));
            }

            return Results.Ok(new CatalogResponse(products, QueryCount: 1));
        })
        .WithSummary("Baseline: one indexed join. The reference point for every other measurement.");

        // Same result set, one query per row. The extra cost is pure round
        // trips, so a single request looks acceptable and concurrency turns it
        // into a pool contention problem. Compare against /products directly.
        group.MapGet("/products/n-plus-one", async (
            NpgsqlDataSource dataSource,
            PathologyOptions options,
            CancellationToken cancellationToken) =>
        {
            await using NpgsqlConnection connection =
                await dataSource.OpenConnectionAsync(cancellationToken);

            List<int> ids = [];
            await using (NpgsqlCommand idQuery = connection.CreateCommand())
            {
                idQuery.CommandText = "SELECT id FROM products ORDER BY id LIMIT $1";
                idQuery.Parameters.AddWithValue(options.NPlusOneRowCount);

                await using NpgsqlDataReader reader = await idQuery.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    ids.Add(reader.GetInt32(0));
                }
            }

            List<ProductView> products = [];
            foreach (int id in ids)
            {
                await using NpgsqlCommand rowQuery = connection.CreateCommand();
                rowQuery.CommandText = """
                    SELECT p.id, p.name, c.name AS category, p.price
                    FROM products p
                    JOIN categories c ON c.id = p.category_id
                    WHERE p.id = $1
                    """;
                rowQuery.Parameters.AddWithValue(id);

                await using NpgsqlDataReader reader = await rowQuery.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    products.Add(new ProductView(
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetDecimal(3)));
                }
            }

            return Results.Ok(new CatalogResponse(products, QueryCount: ids.Count + 1));
        })
        .WithSummary("N+1: one query per row. Cheap alone, quadratic under load.");
    }

    private sealed record ProductView(int Id, string Name, string Category, decimal Price);

    private sealed record CatalogResponse(IReadOnlyList<ProductView> Products, int QueryCount);
}
