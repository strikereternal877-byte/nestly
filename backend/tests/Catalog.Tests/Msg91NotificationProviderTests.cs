using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers <see cref="Msg91NotificationProvider"/> with no network at all, the
/// same <see cref="StubHttpMessageHandler"/> pattern
/// <see cref="PayUPaymentGatewayTests"/>/<c>GoogleMapsRouteEstimateProviderTests</c>
/// use.
///
/// Unlike those two classes' own golden-value tests, nothing here is pinned
/// against a real MSG91 account - see <see cref="Msg91NotificationProvider"/>'s
/// own doc comment for why. These tests pin this project's own request shape
/// against MSG91's documented contract instead, so a future edit cannot
/// silently drift from it even before it is live-verified.
/// </summary>
public sealed class Msg91NotificationProviderTests
{
    private const string AuthKey = "test-auth-key";
    private const string SenderId = "GLAVYX";

    private static Msg91NotificationProvider BuildProvider(StubHttpMessageHandler? handler = null, Msg91Options? options = null) =>
        new(
            new StubHttpClientFactory(handler ?? StubHttpMessageHandler.Responding(HttpStatusCode.OK)),
            Options.Create(options ?? new Msg91Options { AuthKey = AuthKey, SenderId = SenderId }),
            NullLogger<Msg91NotificationProvider>.Instance);

    [Fact]
    public async Task SendSmsAsync_posts_the_documented_request_shape()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson("""{"type":"success","message":"SMS sent successfully"}""");
        var provider = BuildProvider(handler);

        var result = await provider.SendSmsAsync("+919876543210", "Your Glavyx OTP is 123456");

        result.IsSuccess.Should().BeTrue();

        var sent = handler.Requests.Single();
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.ToString().Should().Be("https://api.msg91.com/api/v2/sendsms");
        sent.Header("authkey").Should().Be(AuthKey);

        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;
        root.GetProperty("sender").GetString().Should().Be(SenderId);
        root.GetProperty("route").GetString().Should().Be("4");
        root.GetProperty("country").GetString().Should().Be("91");
        var smsEntry = root.GetProperty("sms")[0];
        smsEntry.GetProperty("message").GetString().Should().Be("Your Glavyx OTP is 123456");
        smsEntry.GetProperty("to")[0].GetString().Should().Be("919876543210");
    }

    [Fact]
    public async Task SendSmsAsync_strips_the_leading_plus_but_makes_no_other_assumption_about_the_number()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson("""{"type":"success"}""");
        var provider = BuildProvider(handler);

        await provider.SendSmsAsync("9876543210", "message");

        var sent = handler.Requests.Single();
        using var body = JsonDocument.Parse(sent.Body);
        body.RootElement.GetProperty("sms")[0].GetProperty("to")[0].GetString().Should().Be("9876543210");
    }

    [Fact]
    public async Task SendSmsAsync_reports_failure_when_MSG91_declines_the_send()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson("""{"type":"error","message":"Invalid sender id"}""");
        var provider = BuildProvider(handler);

        var result = await provider.SendSmsAsync("+919876543210", "message");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Notification.SmsSendFailed");
    }

    [Fact]
    public async Task SendSmsAsync_reports_failure_on_a_non_success_http_status()
    {
        var handler = StubHttpMessageHandler.Responding(HttpStatusCode.Unauthorized);
        var provider = BuildProvider(handler);

        var result = await provider.SendSmsAsync("+919876543210", "message");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task SendSmsAsync_reports_failure_for_an_unparseable_response()
    {
        var handler = StubHttpMessageHandler.Responding(HttpStatusCode.OK, "not json");
        var provider = BuildProvider(handler);

        var result = await provider.SendSmsAsync("+919876543210", "message");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task SendSmsAsync_rejects_a_blank_recipient_without_calling_out_over_the_network()
    {
        var clientFactory = new StubHttpClientFactory(StubHttpMessageHandler.Responding(HttpStatusCode.OK));
        var provider = new Msg91NotificationProvider(
            clientFactory, Options.Create(new Msg91Options { AuthKey = AuthKey, SenderId = SenderId }), NullLogger<Msg91NotificationProvider>.Instance);

        var result = await provider.SendSmsAsync("  ", "message");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Notification.InvalidRecipient");
        clientFactory.RequestedClientNames.Should().BeEmpty();
    }
}
