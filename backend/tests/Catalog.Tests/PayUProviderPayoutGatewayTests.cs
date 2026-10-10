using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application.Payments;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers <see cref="PayUProviderPayoutGateway"/> with no network at all -
/// the same <see cref="StubHttpMessageHandler"/> pattern
/// <c>PayUPaymentGatewayTests</c> uses for PayU's other product. Per the
/// PayU Payouts task brief, this is deliberately NOT the more elaborate
/// reflection/sequenced-response test harness a prior attempt at this exact
/// integration used and stalled on - a single responder that branches on
/// which of the two real PayU endpoints (OAuth token vs. initiate transfer)
/// was called is simple enough for a gateway that only ever makes two calls
/// per <see cref="PayUProviderPayoutGateway.InitiateTransferAsync"/>.
/// </summary>
public sealed class PayUProviderPayoutGatewayTests
{
    private static readonly PayUPayoutOptions ConfiguredOptions = new()
    {
        ClientId = "test-client-id",
        ClientSecret = "test-client-secret",
        PayoutMerchantId = "MERCHANT123",
        BaseUrl = "https://uatoneapi.payu.test/payout/v2/payment",
        AuthBaseUrl = "https://uat-accounts.payu.test/oauth/token",
    };

    private static PayUProviderPayoutGateway BuildGateway(StubHttpMessageHandler handler, PayUPayoutOptions? options = null) =>
        new(new StubHttpClientFactory(handler), Options.Create(options ?? ConfiguredOptions), NullLogger<PayUProviderPayoutGateway>.Instance);

    private static StubHttpMessageHandler RespondingToTokenAndTransfer(string transferJson, HttpStatusCode transferStatus = HttpStatusCode.OK) =>
        new(recorded => recorded.RequestUri!.ToString().Contains("oauth/token", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"test-token","token_type":"Bearer","expires_in":3600}""", Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(transferStatus)
            {
                Content = new StringContent(transferJson, Encoding.UTF8, "application/json"),
            });

    [Fact]
    public void IsConfigured_is_false_when_any_required_field_is_missing()
    {
        var gateway = BuildGateway(StubHttpMessageHandler.Responding(HttpStatusCode.OK), new PayUPayoutOptions { ClientId = "x" });

        gateway.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_is_true_when_every_required_field_is_set()
    {
        var gateway = BuildGateway(StubHttpMessageHandler.Responding(HttpStatusCode.OK));

        gateway.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task InitiateTransferAsync_fetches_a_token_then_posts_the_documented_transfer_fields()
    {
        var handler = RespondingToTokenAndTransfer("""{"status":0,"msg":"Requests are in process...","code":null,"data":[]}""");
        var gateway = BuildGateway(handler);

        var result = await gateway.InitiateTransferAsync(new PayoutTransferRequest(
            MerchantReferenceId: "11111111-1111-1111-1111-111111111111",
            Amount: 1234.50m,
            BeneficiaryAccountNumber: "000111222333",
            BeneficiaryIfscCode: "HDFC0000123",
            BeneficiaryName: "Ravi Kumar"));

        result.Accepted.Should().BeTrue();

        handler.Requests.Should().HaveCount(2);

        var tokenRequest = handler.Requests[0];
        tokenRequest.RequestUri!.ToString().Should().Be(ConfiguredOptions.AuthBaseUrl);
        var tokenForm = HttpUtility.ParseQueryString(tokenRequest.Body);
        tokenForm["grant_type"].Should().Be("client_credentials");
        tokenForm["client_id"].Should().Be("test-client-id");
        tokenForm["client_secret"].Should().Be("test-client-secret");
        tokenForm["scope"].Should().Be("create_payout_transactions");

        var transferRequest = handler.Requests[1];
        transferRequest.RequestUri!.ToString().Should().Be(ConfiguredOptions.BaseUrl);
        transferRequest.Header("Authorization").Should().Be("Bearer test-token");

        using var doc = JsonDocument.Parse(transferRequest.Body);
        var root = doc.RootElement;
        root.GetProperty("beneficiaryAccountNumber").GetString().Should().Be("000111222333");
        root.GetProperty("beneficiaryIfscCode").GetString().Should().Be("HDFC0000123");
        root.GetProperty("beneficiaryName").GetString().Should().Be("Ravi Kumar");
        root.GetProperty("amount").GetString().Should().Be("1234.50");
        root.GetProperty("merchantRefId").GetString().Should().Be("11111111-1111-1111-1111-111111111111");
        root.GetProperty("disableApprovalFlow").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task InitiateTransferAsync_reports_declined_when_PayU_returns_a_non_zero_status()
    {
        var handler = RespondingToTokenAndTransfer("""{"status":1,"msg":"Invalid beneficiary details"}""");
        var gateway = BuildGateway(handler);

        var result = await gateway.InitiateTransferAsync(new PayoutTransferRequest(
            "ref", 100m, "000111222333", "HDFC0000123", "Ravi Kumar"));

        result.Accepted.Should().BeFalse();
        result.FailureReason.Should().Be("Invalid beneficiary details");
    }

    [Fact]
    public async Task InitiateTransferAsync_reports_a_clear_failure_when_PayU_declines_the_token_request()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var gateway = BuildGateway(handler);

        var result = await gateway.InitiateTransferAsync(new PayoutTransferRequest(
            "ref", 100m, "000111222333", "HDFC0000123", "Ravi Kumar"));

        result.Accepted.Should().BeFalse();
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
        // Only the token call was ever attempted - a transfer must never be
        // posted without a real access token.
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public void MatchesConfiguredMerchantId_matches_only_the_exact_configured_id()
    {
        var gateway = BuildGateway(StubHttpMessageHandler.Responding(HttpStatusCode.OK));

        gateway.MatchesConfiguredMerchantId("MERCHANT123").Should().BeTrue();
        gateway.MatchesConfiguredMerchantId("someone-else").Should().BeFalse();
        gateway.MatchesConfiguredMerchantId(null).Should().BeFalse();
    }

    [Fact]
    public async Task InitiateTransferAsync_throws_when_not_configured()
    {
        var gateway = BuildGateway(StubHttpMessageHandler.Responding(HttpStatusCode.OK), new PayUPayoutOptions());

        var act = () => gateway.InitiateTransferAsync(new PayoutTransferRequest("ref", 100m, "000111222333", "HDFC0000123", "Ravi Kumar"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

/// <summary>Covers the safe default's "throw loudly rather than silently no-op" contract - see its own doc comment.</summary>
public sealed class NoOpProviderPayoutGatewayTests
{
    [Fact]
    public void IsConfigured_is_always_false()
    {
        new NoOpProviderPayoutGateway().IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void MatchesConfiguredMerchantId_is_always_false()
    {
        new NoOpProviderPayoutGateway().MatchesConfiguredMerchantId("anything").Should().BeFalse();
    }

    [Fact]
    public async Task InitiateTransferAsync_throws_rather_than_silently_no_opping()
    {
        var gateway = new NoOpProviderPayoutGateway();

        var act = () => gateway.InitiateTransferAsync(new PayoutTransferRequest("ref", 100m, "000111222333", "HDFC0000123", "Ravi Kumar"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
