using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application.Payments;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// <see cref="IProviderPayoutGateway"/> registration. Own file, same
/// reasoning as <see cref="PaymentGatewayRegistration"/>: the implementation
/// is chosen by configuration rather than fixed.
/// </summary>
internal static class ProviderPayoutGatewayRegistration
{
    /// <summary>
    /// Registers real PayU Payouts when <see cref="PayUPayoutOptions"/> is
    /// fully configured, and <see cref="NoOpProviderPayoutGateway"/>
    /// otherwise - mirrors <see cref="PaymentGatewayRegistration.AddPaymentGateway"/>'s
    /// swap condition exactly, except the "unconfigured" branch here throws
    /// rather than simulating a gateway (see <see cref="NoOpProviderPayoutGateway"/>'s
    /// own doc comment for why there is no sandbox equivalent for a real
    /// bank transfer).
    /// </summary>
    internal static IServiceCollection AddProviderPayoutGateway(this IServiceCollection services, IConfiguration configuration)
    {
        // Not a secret a process cannot start without - PayU Payouts
        // credentials are optional (manual bank transfer remains fully
        // available without them, product decision) - so no
        // ValidateOnStart, same reasoning as PayUOptions above it.
        services
            .AddOptions<PayUPayoutOptions>()
            .Bind(configuration.GetSection(PayUPayoutOptions.SectionName));

        services.AddHttpClient(PayUProviderPayoutGateway.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<NoOpProviderPayoutGateway>();

        services.AddSingleton<IProviderPayoutGateway>(serviceProvider =>
        {
            var payoutOptions = serviceProvider.GetRequiredService<IOptions<PayUPayoutOptions>>().Value;
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ProviderPayoutGatewayRegistration));

            if (!payoutOptions.IsConfigured)
            {
                // Logged at startup so "why did Pay via PayU throw?" is
                // answerable from the logs without reading configuration -
                // same convention as PaymentGatewayRegistration.
                logger.LogInformation(
                    "Provider payouts will not offer PayU: PayU Payouts is {State}.",
                    payoutOptions.Enabled ? "missing a client id, secret, merchant id, or base URL" : "disabled by configuration");
                return serviceProvider.GetRequiredService<NoOpProviderPayoutGateway>();
            }

            logger.LogInformation("Provider payouts will offer real PayU Payouts transfers.");
            return ActivatorUtilities.CreateInstance<PayUProviderPayoutGateway>(serviceProvider);
        });

        return services;
    }
}
