using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.BuildingBlocks.Privacy;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Real outbound SMS via MSG91's REST API - swapped in for sandbox-simulated
/// SMS once <see cref="Msg91Options"/> is fully configured, see
/// <see cref="NotificationRegistration"/> for the swap condition.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="PayUPaymentGateway"/>'s hash formulas - verified against
/// PayU's real test environment while that class was written - this class's
/// exact request/response shape has NOT been exercised against a real MSG91
/// account: no MSG91 credentials existed to test against while writing it.
/// It follows MSG91's long-documented v2 "Send SMS" API (<c>authkey</c>
/// header, JSON body, transactional route "4"), the most stable and widely
/// referenced of MSG91's several SMS APIs, but confirm the request actually
/// succeeds against a real MSG91 trial account before relying on it for
/// real customer OTPs - the same caution <c>PayUPaymentGateway.VerifyOrderStatusAsync</c>'s
/// own doc comment flags for the same reason.
/// </para>
/// <para>
/// Sending to an Indian mobile number at all - regardless of vendor -
/// additionally requires <see cref="Msg91Options.SenderId"/> and this
/// method's <paramref name="message"/> text to both match a DLT template
/// registered with MSG91 and approved by TRAI; an unregistered sender ID or
/// message shape is typically dropped silently by the telecom operator, not
/// rejected by MSG91's API with an error this code could detect and surface.
/// </para>
/// </remarks>
public sealed class Msg91NotificationProvider : INotificationProvider
{
    /// <summary>Named <see cref="HttpClient"/> registration - see <see cref="SupabaseFileStorageService.HttpClientName"/> for why named rather than typed.</summary>
    public const string HttpClientName = "Msg91.Sms";

    private static readonly JsonSerializerOptions ResponseJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Msg91Options _options;
    private readonly ILogger<Msg91NotificationProvider> _logger;

    public Msg91NotificationProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<Msg91Options> options,
        ILogger<Msg91NotificationProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// This class exists solely for SMS - <see cref="NotificationRegistration"/>
    /// always wraps it in a <see cref="CompositeNotificationProvider"/> that
    /// routes email elsewhere, so this is never actually reached in practice.
    /// Simulates rather than throwing regardless - a "never crash on a
    /// vendor gap" posture in case this type is ever resolved standalone.
    /// </summary>
    public Task<Result> SendEmailAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            return Task.FromResult(Result.Failure(Error.Validation("Notification.InvalidRecipient", "Email address is required.")));
        }

        _logger.LogInformation("Sandbox email simulated for {MaskedEmail}", ContactMasking.Mask(toEmail));
        return Task.FromResult(Result.Success());
    }

    public async Task<Result> SendSmsAsync(string toMobile, string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toMobile))
        {
            return Result.Failure(Error.Validation("Notification.InvalidRecipient", "Mobile number is required."));
        }

        // MSG91's "to" field is digits only (country code, no leading "+") -
        // this codebase's mobile numbers are stored/passed in whatever shape
        // the caller has them, so only the "+" itself is stripped rather than
        // assuming a specific country-code convention this class cannot be
        // sure of.
        string recipient = toMobile.TrimStart('+');

        var payload = new Msg91SendSmsRequest(
            _options.SenderId!,
            _options.Route,
            "91",
            [new Msg91SmsEntry(message, [recipient])]);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.msg91.com/api/v2/sendsms")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("authkey", _options.AuthKey);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "MSG91 SMS send threw for a request that never got a response.");
            return Result.Failure(Error.Business("Notification.SmsSendFailed", "Failed to send the SMS."));
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Status code only, never the response body - same discipline
                // as BrevoNotificationProvider: a vendor's error body can
                // echo request details (including the message text, which
                // carries an OTP code) that don't belong in logs.
                _logger.LogError("MSG91 SMS send failed with status {StatusCode}.", (int)response.StatusCode);
                return Result.Failure(Error.Business("Notification.SmsSendFailed", "Failed to send the SMS."));
            }

            Msg91SendSmsResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<Msg91SendSmsResponse>(body, ResponseJsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "MSG91 SMS response could not be parsed.");
                return Result.Failure(Error.Business("Notification.SmsSendFailed", "Failed to send the SMS."));
            }

            if (parsed is null || !string.Equals(parsed.Type, "success", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("MSG91 declined an SMS send: {Type}.", parsed?.Type ?? "unparseable response");
                return Result.Failure(Error.Business("Notification.SmsSendFailed", "Failed to send the SMS."));
            }
        }

        // Never logs the message body - an OTP code lives in it, same
        // no-secrets-in-logs rule BrevoNotificationProvider follows.
        _logger.LogInformation("SMS sent to {MaskedMobile} via MSG91.", ContactMasking.Mask(toMobile));
        return Result.Success();
    }

    private sealed record Msg91SendSmsRequest(
        [property: JsonPropertyName("sender")] string Sender,
        [property: JsonPropertyName("route")] string Route,
        [property: JsonPropertyName("country")] string Country,
        [property: JsonPropertyName("sms")] Msg91SmsEntry[] Sms);

    private sealed record Msg91SmsEntry(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("to")] string[] To);

    private sealed class Msg91SendSmsResponse
    {
        public string? Type { get; set; }
        public string? Message { get; set; }
    }
}
