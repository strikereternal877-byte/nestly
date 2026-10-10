using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application.Payments;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// <see cref="IPaymentGateway"/> registration. Own file, same reasoning as
/// <see cref="FileStorageRegistration"/>: the implementation is chosen by
/// configuration rather than fixed.
/// </summary>
internal static class PaymentGatewayRegistration
{
    /// <summary>
    /// Registers real PayU Hosted Checkout when <see cref="PayUOptions"/> is
    /// fully configured, and the sandbox otherwise.
    /// <see cref="ISandboxPaymentSimulator"/> is always bound to the concrete
    /// sandbox regardless of this choice - it is a sandbox-only capability
    /// (see its own doc comment), never resolved through
    /// <see cref="IPaymentGateway"/>.
    /// </summary>
    internal static IServiceCollection AddPaymentGateway(this IServiceCollection services, IConfiguration configuration)
    {
        // Not a secret a process cannot start without - PayU credentials are
        // optional by design (this project ships with the sandbox) - so no
        // ValidateOnStart, same reasoning as SupabaseStorageOptions.
        services
            .AddOptions<PayUOptions>()
            .Bind(configuration.GetSection(PayUOptions.SectionName));

        services.AddHttpClient(PayUPaymentGateway.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        // Stateless - depends only on bound Options - so one shared instance
        // safely serves both interfaces (SandboxPaymentGateway implements
        // IPaymentGateway and the sandbox-only ISandboxPaymentSimulator).
        services.AddSingleton<SandboxPaymentGateway>();
        services.AddSingleton<ISandboxPaymentSimulator>(sp => sp.GetRequiredService<SandboxPaymentGateway>());

        services.AddSingleton<IPaymentGateway>(serviceProvider =>
        {
            var payUOptions = serviceProvider.GetRequiredService<IOptions<PayUOptions>>().Value;
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PaymentGatewayRegistration));

            if (!payUOptions.IsConfigured)
            {
                // Logged at startup so "why did a real payment attempt hit
                // the sandbox?" is answerable from the logs without reading
                // configuration - same convention as every other real-vendor
                // registration in this project.
                logger.LogInformation(
                    "Payments will use the sandbox gateway: PayU is {State}.",
                    payUOptions.Enabled ? "missing a merchant key, salt, or checkout return URL" : "disabled by configuration");
                return serviceProvider.GetRequiredService<SandboxPaymentGateway>();
            }

            logger.LogInformation("Payments will use real PayU Hosted Checkout ({Environment}).", payUOptions.UseProductionEnvironment ? "production" : "test");
            return ActivatorUtilities.CreateInstance<PayUPaymentGateway>(serviceProvider);
        });

        return services;
    }
}
