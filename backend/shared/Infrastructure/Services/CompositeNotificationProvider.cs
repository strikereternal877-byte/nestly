using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Combines an independently-chosen email provider and SMS provider into
/// one <see cref="INotificationProvider"/> (SRS 30.2). Exists so
/// <see cref="NotificationRegistration"/> can wire real Brevo email and real
/// MSG91 SMS at the same time without either provider depending on the
/// other - that mutual dependency was the original, broken design (each
/// decorating the other's channel via constructor injection), which
/// deadlocks the container the moment both channels are configured
/// simultaneously, since resolving either one requires first resolving the
/// <see cref="INotificationProvider"/> registration that is still in the
/// middle of being constructed. This composite is the standard fix: each
/// half is single-purpose and self-contained, and only this class combines
/// them.
/// </summary>
internal sealed class CompositeNotificationProvider : INotificationProvider
{
    private readonly INotificationProvider _emailProvider;
    private readonly INotificationProvider _smsProvider;

    public CompositeNotificationProvider(INotificationProvider emailProvider, INotificationProvider smsProvider)
    {
        _emailProvider = emailProvider;
        _smsProvider = smsProvider;
    }

    public Task<Result> SendSmsAsync(string toMobile, string message, CancellationToken cancellationToken = default) =>
        _smsProvider.SendSmsAsync(toMobile, message, cancellationToken);

    public Task<Result> SendEmailAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) =>
        _emailProvider.SendEmailAsync(toEmail, subject, body, cancellationToken);
}
