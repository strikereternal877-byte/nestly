using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.BuildingBlocks.Privacy;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Real outbound email via Brevo's transactional email REST API, preferred
/// over <see cref="SmtpNotificationProvider"/>'s Gmail SMTP path once
/// <see cref="BrevoOptions"/> is configured - see
/// <see cref="NotificationRegistration"/> for the swap order. Chosen over
/// Gmail for anything beyond light testing: Brevo's free tier is a
/// genuinely recurring daily quota (no card, no expiring trial credit),
/// and dedicated transactional-email infrastructure deliverable to inboxes
/// more reliably than a personal Gmail account sending in bulk.
/// </summary>
/// <remarks>
/// Uses Brevo's plain REST endpoint (a bearer-style API key header, JSON
/// body) rather than a vendor SDK - no new dependency is needed, only
/// <see cref="IHttpClientFactory"/>, the same choice
/// <see cref="SupabaseFileStorageService"/> and
/// <see cref="Msg91NotificationProvider"/> made for the same reason.
/// </remarks>
public sealed class BrevoNotificationProvider : INotificationProvider
{
    /// <summary>Named <see cref="HttpClient"/> registration - see <see cref="SupabaseFileStorageService.HttpClientName"/> for why named rather than typed.</summary>
    public const string HttpClientName = "Brevo.Email";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BrevoOptions _options;
    private readonly ILogger<BrevoNotificationProvider> _logger;

    public BrevoNotificationProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<BrevoOptions> options,
        ILogger<BrevoNotificationProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// This class exists solely for email - <see cref="NotificationRegistration"/>
    /// always wraps it in a <see cref="CompositeNotificationProvider"/> that
    /// routes SMS elsewhere, so this is never actually reached in practice.
    /// Simulates rather than throwing regardless, matching
    /// <see cref="SandboxNotificationProvider"/>'s "never crash on a vendor
    /// gap" posture in case this type is ever resolved standalone.
    /// </summary>
    public Task<Result> SendSmsAsync(string toMobile, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toMobile))
        {
            return Task.FromResult(Result.Failure(Error.Validation("Notification.InvalidRecipient", "Mobile number is required.")));
        }

        _logger.LogInformation("Sandbox SMS simulated for {MaskedMobile}", ContactMasking.Mask(toMobile));
        return Task.FromResult(Result.Success());
    }

    public async Task<Result> SendEmailAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            return Result.Failure(Error.Validation("Notification.InvalidRecipient", "Email address is required."));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "v3/smtp/email");
        request.Headers.Add("api-key", _options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new
        {
            sender = new { name = _options.FromName, email = _options.FromEmail },
            to = new[] { new { email = toEmail } },
            subject,
            textContent = body,
        });

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Status code only, never the response body - same discipline as
            // SupabaseFileStorageService/Msg91NotificationProvider: Brevo's
            // error bodies can echo request details that don't belong in logs.
            _logger.LogError("Brevo email send failed with status {StatusCode}.", (int)response.StatusCode);
            return Result.Failure(Error.Business("Notification.EmailSendFailed", "Failed to send the email."));
        }

        // Never logs subject/body - an OTP code lives in the body, same
        // no-secrets-in-logs rule SmtpNotificationProvider follows.
        _logger.LogInformation("Email sent to {MaskedEmail} via Brevo.", ContactMasking.Mask(toEmail));
        return Result.Success();
    }
}
