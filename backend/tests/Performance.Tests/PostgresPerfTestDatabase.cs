using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Interceptors;
using Npgsql;

namespace Nestly.Performance.Tests;

/// <summary>
/// A throwaway <see cref="NestlyDbContext"/> backed by a real PostgreSQL
/// server - the actual production database engine, unlike
/// <see cref="PerfTestDatabase"/>'s SQLite.
///
/// <para>
/// <b>Why this exists alongside PerfTestDatabase, not instead of it.</b>
/// <see cref="ConcurrentSlotBookingPerformanceTests"/> proves
/// <c>SlotCapacityRepository</c>'s atomic-conditional-UPDATE reservation is
/// logically correct - no more bookings than capacity, ever - using a
/// file-based SQLite database in WAL mode, which genuinely serializes
/// concurrent writers rather than erroring on contention. That proof is
/// sound as far as it goes, but it has never exercised the real engine this
/// runs against in production: EF Core's <c>ExecuteUpdateAsync</c> is
/// translated per-provider, Npgsql's own connection pooling and unique-
/// constraint-violation surface differ from Microsoft.Data.Sqlite's, and
/// SQLite's single-writer model cannot say anything about Postgres
/// row-level lock behavior under many genuinely concurrent connections
/// (docs/PRODUCTION-READINESS.md §4.3: "this needs a real load harness
/// against a running stack"). This fixture is that harness's database half;
/// <see cref="ConcurrentSlotBookingLoadTests"/> is the tests that use it.
/// </para>
///
/// <para>
/// <b>Isolation without CREATEDB.</b> The local/CI Postgres role this
/// connects as does not have the CREATEDB privilege (matching the
/// principle-of-least-privilege posture the rest of this platform's
/// database access follows), so each fixture instance gets its own
/// PostgreSQL <em>schema</em> instead of its own database - set via the
/// connection string's <c>Search Path</c>, not via any change to
/// <see cref="NestlyDbContext"/> itself (it declares no default schema, so
/// it already follows whatever the connection's search_path resolves to).
/// The schema is created fresh and dropped <c>CASCADE</c> on dispose, so
/// concurrent test runs (or a run against a long-lived local dev Postgres)
/// never collide with each other or with real data.
/// </para>
///
/// <para>
/// <b>Deliberately no graceful skip when Postgres is unreachable.</b> A
/// load test that silently skips when its one precondition is missing gives
/// false confidence - exactly the failure mode
/// docs/PRODUCTION-READINESS.md §4.1 describes for a quality gate that
/// stops gating. This throws instead, the same way the `backend` CI job's
/// tests simply fail without their Postgres service container rather than
/// skipping. ci.yml's `load-test` job always provides one.
/// </para>
/// </summary>
public sealed class PostgresPerfTestDatabase : IDisposable
{
    /// <summary>
    /// Overridable so CI can point this at its own service container without
    /// touching test code. Defaults to this repo's standard local dev
    /// Postgres (docker-compose.yml / appsettings.Development.json), the
    /// same convention <c>NestlyDbContextFactory</c> uses for design-time
    /// tooling.
    /// </summary>
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=nestly;Username=nestly;Password=nestly_dev";

    private readonly string _schema = $"loadtest_{Guid.NewGuid():N}";
    private readonly string _baseConnectionString;

    public PostgresPerfTestDatabase()
    {
        _baseConnectionString =
            Environment.GetEnvironmentVariable("LOADTEST_POSTGRES_CONNECTION") ?? DefaultConnectionString;

        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = _schema,
            // Explicit, not left at Npgsql's own default (also 100): a
            // standard Postgres server's max_connections is 100 too (the
            // postgres:16-alpine image ci.yml already uses, and this
            // fixture's own local default), so leaving both at 100 leaves
            // zero headroom for the schema-admin connections this fixture
            // opens directly, any other session sharing the server, and
            // Postgres's own superuser-reserved slots. 80 leaves that
            // headroom; MaxConcurrentCustomers below is sized to fit under it.
            MaxPoolSize = 80,
        };

        Options = new DbContextOptionsBuilder<NestlyDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .UseSnakeCaseNamingConvention()
            // Same fix DependencyInjection.AddInfrastructure wires up for the
            // real app - see NewOwnedChildEntityInterceptor's doc comment.
            .AddInterceptors(new NewOwnedChildEntityInterceptor())
            .Options;

        using (var admin = new NpgsqlConnection(_baseConnectionString))
        {
            admin.Open();
            using var create = new NpgsqlCommand($"CREATE SCHEMA \"{_schema}\"", admin);
            create.ExecuteNonQuery();
        }

        using var context = new NestlyDbContext(Options);
        // Not context.Database.EnsureCreated(): its relational implementation
        // checks whether the *database* exists first, and short-circuits
        // without creating any tables when it does - which for this fixture
        // is always true, since we deliberately reuse the existing `nestly`
        // database (see class doc comment on why: no CREATEDB privilege) and
        // isolate via schema instead. CreateTables() issues the same
        // model-derived DDL without that pre-check.
        context.GetService<IRelationalDatabaseCreator>().CreateTables();
    }

    public DbContextOptions<NestlyDbContext> Options { get; }

    /// <summary>A fresh context, on its own connection from the pool - what lets many simulated concurrent bookings hit the server truly in parallel, not just truly concurrently queued as SQLite's single writer does.</summary>
    public NestlyDbContext CreateContext() => new(Options);

    public void Dispose()
    {
        using var admin = new NpgsqlConnection(_baseConnectionString);
        admin.Open();
        using var drop = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE", admin);
        drop.ExecuteNonQuery();
    }
}
