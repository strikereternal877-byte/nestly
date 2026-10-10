using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nestly.BuildingBlocks.Privacy;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Sandbox <see cref="INotificationProvider"/> for local/dev environments: it
/// never calls a real SMS/email vendor and, outside <see cref="IHostEnvironment.IsDevelopment"/>,
/// never logs message content (which may carry an OTP) - only that a send
/// was simulated, matching the no-PII/no-secrets logging rule the OTP
/// service itself already follows. A real vendor integration implements the
/// same interface and is swapped in via <c>AddInfrastructure</c> per
/// environment - see <c>EmailOptions.AppPassword</c>'s doc comment for how to
/// configure one locally instead of relying on the console.
///
/// <para>
/// In Development only, the full message (which is how an OTP code actually
/// reaches a developer who has not configured a real Gmail/Brevo/MSG91
/// account) is also logged - there is no other way to complete OTP-gated
/// flows locally without one, and unlike Staging/Production this environment
/// never holds a real customer's data. Never enabled by an app setting or
/// environment variable a deployment could flip; only <see cref="IHostEnvironment.IsDevelopment"/>
/// gates it, so it can never leak into a real environment via configuration
/// drift.
/// </para>
/// </summary>
public class SandboxNotificationProvider : INotificationProvider
{
    private readonly ILogger<SandboxNotificationProvider> _logger;
    private readonly bool _isDevelopment;

    public SandboxNotificationProvider(ILogger<SandboxNotificationProvider> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _isDevelopment = environment.IsDevelopment();
    }

    public Task<Result> SendSmsAsync(string toMobile, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toMobile))
        {
            return Task.FromResult(Result.Failure(Error.Validation("Notification.InvalidRecipient", "Mobile number is required.")));
        }

        if (_isDevelopment)
        {
            _logger.LogInformation("[DEV ONLY] Sandbox SMS to {MaskedMobile}: {Message}", ContactMasking.Mask(toMobile), message);
        }
        else
        {
            _logger.LogInformation("Sandbox SMS simulated for {MaskedMobile}", ContactMasking.Mask(toMobile));
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result> SendEmailAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            return Task.FromResult(Result.Failure(Error.Validation("Notification.InvalidRecipient", "Email address is required.")));
        }

        if (_isDevelopment)
        {
            _logger.LogInformation("[DEV ONLY] Sandbox email to {MaskedEmail}, subject \"{Subject}\": {Body}", ContactMasking.Mask(toEmail), subject, body);
        }
        else
        {
            _logger.LogInformation("Sandbox email simulated for {MaskedEmail}", ContactMasking.Mask(toEmail));
        }

        return Task.FromResult(Result.Success());
    }
}
