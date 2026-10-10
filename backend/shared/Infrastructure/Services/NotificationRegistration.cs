using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Domain;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// <see cref="INotificationProvider"/> registration (SRS 30.2). Own file,
/// same reasoning as <see cref="PushNotificationRegistration"/>: email and
/// SMS are each chosen independently by configuration rather than fixed, and
/// that choice deserves to be readable on its own rather than growing inline
/// in <c>DependencyInjection.AddInfrastructure</c>.
/// </summary>
internal static class NotificationRegistration
{
    /// <summary>
    /// Registers real email delivery - Brevo when <see cref="BrevoOptions"/>
    /// is configured, else Gmail SMTP once <c>Email:AppPassword</c> is set -
    /// and real MSG91 SMS once every <see cref="Msg91Options"/> credential is
    /// set. Email and SMS are chosen independently of each other, since one
    /// channel being real says nothing about the other. Any or all fall back
    /// to the sandbox provider's simulated behaviour when unconfigured.
    /// </summary>
    internal static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        // Reading the raw config value here rather than IOptions<T> because
        // this decision runs at registration time, before the container that
        // would resolve IOptions<T> exists yet - same reasoning the previous
        // inline version of this check used.
        services
            .AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateDataAnnotations();

        // Neither section holds anything a process cannot start without -
        // all three are optional by design - so no ValidateOnStart, same
        // reasoning as SupabaseStorageOptions/FirebaseOptions.
        services
            .AddOptions<BrevoOptions>()
            .Bind(configuration.GetSection(BrevoOptions.SectionName));

        services
            .AddOptions<Msg91Options>()
            .Bind(configuration.GetSection(Msg91Options.SectionName));

        services.AddHttpClient(BrevoNotificationProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.brevo.com/");
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        // No BaseAddress - Msg91NotificationProvider sends a fully-qualified
        // URL itself, same reasoning as PayUPaymentGateway's own HttpClient
        // registration.
        services.AddHttpClient(Msg91NotificationProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        services.AddScoped<SandboxNotificationProvider>();
        services.AddScoped<SmtpNotificationProvider>();
        services.AddScoped<BrevoNotificationProvider>();
        services.AddScoped<Msg91NotificationProvider>();

        services.AddScoped<INotificationProvider>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NotificationRegistration));

            // Each channel is resolved independently, then combined by
            // CompositeNotificationProvider - never one resolved from
            // inside the other's construction, which is what caused the
            // circular-dependency bug this design replaced. Logged at
            // startup so "why didn't the email/SMS arrive?" is answerable
            // from the logs without reading configuration - same convention
            // as PushNotificationRegistration/RouteEstimateRegistration.
            var brevoOptions = serviceProvider.GetRequiredService<IOptions<BrevoOptions>>().Value;
            var emailConfigured = !string.IsNullOrWhiteSpace(
                configuration[$"{EmailOptions.SectionName}:{nameof(EmailOptions.AppPassword)}"]);

            INotificationProvider emailProvider;
            if (brevoOptions.IsConfigured)
            {
                logger.LogInformation("Email notifications will use real Brevo delivery.");
                emailProvider = serviceProvider.GetRequiredService<BrevoNotificationProvider>();
            }
            else if (emailConfigured)
            {
                logger.LogInformation("Email notifications will use real Gmail SMTP delivery.");
                emailProvider = serviceProvider.GetRequiredService<SmtpNotificationProvider>();
            }
            else
            {
                logger.LogInformation("Email notifications will use the sandbox provider: neither Brevo nor Email:AppPassword is configured.");
                emailProvider = serviceProvider.GetRequiredService<SandboxNotificationProvider>();
            }

            var msg91Options = serviceProvider.GetRequiredService<IOptions<Msg91Options>>().Value;
            INotificationProvider smsProvider;
            if (msg91Options.IsConfigured)
            {
                logger.LogInformation("SMS notifications will use MSG91.");
                smsProvider = serviceProvider.GetRequiredService<Msg91NotificationProvider>();
            }
            else
            {
                logger.LogInformation(
                    "SMS notifications will use the sandbox provider: MSG91 is {State}.",
                    msg91Options.Enabled ? "missing an auth key or sender id" : "disabled by configuration");
                smsProvider = serviceProvider.GetRequiredService<SandboxNotificationProvider>();
            }

            return ReferenceEquals(emailProvider, smsProvider)
                ? emailProvider
                : new CompositeNotificationProvider(emailProvider, smsProvider);
        });

        return services;
    }
}
