using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nestly.Infrastructure.Persistence.Migrations;

/// <summary>
/// Opt-in schema catch-up for a database an operator cannot reach with
/// <c>database/scripts/apply-migrations.sh</c> directly - eg. a managed
/// database whose credentials live only in this service's own connection
/// string, not in anything an operator's shell can construct. Shaped as an
/// <see cref="IApplicationBuilder"/> extension called from <c>Program.cs</c>
/// for the same reason <c>AdminPermissionReconciliationExtensions
/// .ReconcileAdminPermissions</c> is: the work needs a built
/// <see cref="IServiceProvider"/> to resolve a scoped
/// <see cref="NestlyDbContext"/> from.
/// </summary>
public static class StartupMigrationExtensions
{
    /// <summary>
    /// <c>Migrations:ApplyOnStartup</c> / env <c>Migrations__ApplyOnStartup</c>.
    /// Off by default and not set in any committed appsettings file - this
    /// exists to be switched on for a single deploy or restart against a
    /// specific target, then switched back off.
    /// </summary>
    private const string ApplyOnStartupKey = "Migrations:ApplyOnStartup";

    /// <summary>
    /// Applies any pending EF Core migrations before the host starts serving
    /// requests, but only when <see cref="ApplyOnStartupKey"/> is "true".
    ///
    /// <para>
    /// Safe to call from all three API hosts: they share one
    /// <see cref="NestlyDbContext"/> / migrations history against one
    /// database, so whichever host is restarted with the flag set catches
    /// the database up for all three. It is deliberately not left on by
    /// default - EF Core's <c>Migrate()</c> takes no distributed lock, so two
    /// instances racing to apply the same pending migration on a cold start
    /// is a real failure mode the moment this runs behind more than one
    /// replica of the same host. Flip it on, restart once, confirm the
    /// "migrations applied" log line, flip it back off.
    /// </para>
    ///
    /// <para>
    /// Unlike <c>ReconcileAdminPermissions</c> and
    /// <c>ReportBookabilityReadiness</c>, a failure here is logged and
    /// rethrown rather than swallowed: those two are best-effort safeguards
    /// that must not turn a hiccup into an outage, but an operator who
    /// explicitly opted into this flag needs to know immediately and
    /// unambiguously if the schema catch-up they asked for did not happen -
    /// not find out later from whatever broke downstream because it didn't.
    /// </para>
    /// </summary>
    public static IApplicationBuilder ApplyPendingMigrationsIfConfigured(this IApplicationBuilder app)
    {
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue<bool>(ApplyOnStartupKey))
        {
            return app;
        }

        using var scope = app.ApplicationServices.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<NestlyDbContext>>();
        var dbContext = scope.ServiceProvider.GetRequiredService<NestlyDbContext>();

        try
        {
            var pending = dbContext.Database.GetPendingMigrations().ToList();
            if (pending.Count == 0)
            {
                logger.LogInformation(
                    "{ConfigKey} is set but there are no pending migrations; nothing to do.",
                    ApplyOnStartupKey);
                return app;
            }

            logger.LogWarning(
                "{ConfigKey} is set - applying {Count} pending migration(s): {Migrations}",
                ApplyOnStartupKey,
                pending.Count,
                string.Join(", ", pending));

            dbContext.Database.Migrate();

            logger.LogWarning(
                "Pending migrations applied successfully. Turn {ConfigKey} back off before the next deploy or restart.",
                ApplyOnStartupKey);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "{ConfigKey} was set but applying pending migrations failed; the schema catch-up did not complete.",
                ApplyOnStartupKey);
            throw;
        }

        return app;
    }
}
