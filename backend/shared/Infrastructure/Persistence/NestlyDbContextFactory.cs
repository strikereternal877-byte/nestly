using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nestly.Infrastructure.Persistence;

/// <summary>
/// Design-time <see cref="NestlyDbContext"/> construction for the EF Core
/// tools (<c>dotnet ef migrations add</c> / <c>database update</c>).
///
/// Without this, the tools need a startup project that both references
/// <c>Microsoft.EntityFrameworkCore.Design</c> and builds its host - which in
/// practice meant ConsumerApi, so scaffolding a migration was impossible
/// whenever that API happened to be running (its DLLs are locked). This
/// factory lets Infrastructure stand alone as its own startup project:
///
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project backend/shared/Infrastructure \
///     --startup-project backend/shared/Infrastructure -o ../../../database/migrations
/// </code>
///
/// <c>-o</c> is resolved relative to <c>--project</c>, not the working
/// directory (confirmed the hard way, 2026-09-09) - the shorter-looking
/// <c>-o database/migrations</c> silently lands at
/// <c>backend/shared/Infrastructure/database/migrations</c> instead of this
/// repo's actual, already-populated <c>database/migrations</c>, and its
/// build then fails with a duplicate <c>NestlyDbContextModelSnapshot</c>
/// once a real one already exists there too. Separately: EF always
/// regenerates the snapshot at this project's default <c>Migrations/</c>
/// folder as well, regardless of <c>-o</c> - copy that copy over
/// <c>database/migrations/NestlyDbContextModelSnapshot.cs</c> and delete the
/// stray <c>Migrations/</c> folder afterward, every time.
///
/// It is only ever used by the CLI tools - the running APIs configure their
/// own context through <c>DependencyInjection</c> and never touch this.
/// </summary>
public sealed class NestlyDbContextFactory : IDesignTimeDbContextFactory<NestlyDbContext>
{
    /// <summary>Matches the "Database" key the APIs use, so one env var overrides both.</summary>
    private const string ConnectionStringEnvironmentVariable = "ConnectionStrings__Database";

    /// <summary>
    /// The docker-compose/appsettings.Development dev database. Only a
    /// fallback for local tooling - never a production credential, and never
    /// read by the running APIs (which resolve configuration normally).
    /// </summary>
    private const string LocalDevelopmentConnectionString =
        "Host=localhost;Port=5432;Database=nestly;Username=nestly;Password=nestly_dev";

    public NestlyDbContext CreateDbContext(string[] args)
    {
        string connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
            ?? LocalDevelopmentConnectionString;

        // Must mirror DependencyInjection's AddDbContext configuration exactly.
        // UseSnakeCaseNamingConvention in particular is not optional here: the
        // whole schema is snake_case, so omitting it makes EF diff every table
        // and constraint as "needs renaming" and emit a destructive migration
        // instead of the intended change.
        var optionsBuilder = new DbContextOptionsBuilder<NestlyDbContext>();
        optionsBuilder
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();

        return new NestlyDbContext(optionsBuilder.Options);
    }
}
