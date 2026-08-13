using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application.Storage;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// <see cref="IFileStorageService"/> registration. Own file, same reasoning
/// as <see cref="RouteEstimateRegistration"/>: the implementation is chosen
/// by configuration rather than fixed.
/// </summary>
internal static class FileStorageRegistration
{
    /// <summary>
    /// Registers Supabase Storage when credentials are configured and the
    /// local-disk implementation otherwise.
    /// </summary>
    internal static IServiceCollection AddFileStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.SectionName))
            .ValidateDataAnnotations();

        // Neither section holds anything a process cannot start without -
        // Supabase credentials are optional by design - so no
        // ValidateOnStart, same reasoning as GoogleMapsOptions.
        services
            .AddOptions<SupabaseStorageOptions>()
            .Bind(configuration.GetSection(SupabaseStorageOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddHttpClient(SupabaseFileStorageService.HttpClientName, (serviceProvider, client) =>
        {
            var supabaseOptions = serviceProvider.GetRequiredService<IOptions<SupabaseStorageOptions>>().Value;
            if (supabaseOptions.IsConfigured)
            {
                client.BaseAddress = new Uri(supabaseOptions.ProjectUrl!);
            }

            client.Timeout = TimeSpan.FromSeconds(supabaseOptions.TimeoutSeconds);
        });

        // Always registered as a concrete type - the always-available
        // fallback, same shape as SandboxRouteEstimateProvider.
        services.AddSingleton<LocalDiskFileStorageService>();

        services.AddSingleton<IFileStorageService>(serviceProvider =>
        {
            var supabaseOptions = serviceProvider.GetRequiredService<IOptions<SupabaseStorageOptions>>().Value;
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(FileStorageRegistration));

            if (!supabaseOptions.IsConfigured)
            {
                // Logged at startup so "why don't uploaded images show up on
                // my other machine?" is answerable from the logs without
                // reading configuration.
                logger.LogInformation(
                    "File uploads will use local disk (App_Data/uploads): Supabase Storage is {State}.",
                    supabaseOptions.Enabled ? "missing a project URL or service role key" : "disabled by configuration");

                return serviceProvider.GetRequiredService<LocalDiskFileStorageService>();
            }

            logger.LogInformation("File uploads will use Supabase Storage.");
            return ActivatorUtilities.CreateInstance<SupabaseFileStorageService>(serviceProvider);
        });

        return services;
    }
}
