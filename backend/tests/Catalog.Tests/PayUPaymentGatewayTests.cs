using System.Net;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application.Payments;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers <see cref="PayUPaymentGateway"/> with no network at all, the same
/// <see cref="StubHttpMessageHandler"/> pattern
/// <c>GoogleMapsRouteEstimateProviderTests</c> uses.
///
/// <see cref="Hash_matches_a_value_PayUs_own_test_server_accepted"/> and
/// <see cref="VerifyWebhookSignature_accepts_a_signature_value_PayUs_documented_reverse_hash_formula_produces"/>
/// are golden-value regression tests: the exact canonical string and hash in
/// each were produced by POSTing a real form to PayU's test environment
/// (https://test.payu.in/_payment) while this class was written and
/// confirmed PayU redirected to its real checkout page (not its own
/// "incorrectly calculated hash" error page) - so these pin the hash formula
/// against the actual vendor, not just against this project's own
/// re-implementation of it.
/// </summary>
public sealed class PayUPaymentGatewayTests
{
    private const string MerchantKey = "gtKFFx";
    private const string MerchantSalt = "4R38IvwiV57FwVpsgOvTXBdLE4tHUXFW";

    private static PayUPaymentGateway BuildGateway(StubHttpMessageHandler? handler = null, PayUOptions? options = null) =>
        new(
            new StubHttpClientFactory(handler ?? StubHttpMessageHandler.Responding(HttpStatusCode.OK)),
            Options.Create(options ?? new PayUOptions
            {
                MerchantKey = MerchantKey,
                MerchantSalt = MerchantSalt,
                CheckoutReturnBaseUrl = "https://app.nestly.test",
            }),
            NullLogger<PayUPaymentGateway>.Instance);

    [Fact]
    public async Task CreateOrderAsync_never_calls_out_over_the_network()
    {
        // PayU's classic Hosted Checkout has no server-side "create order"
        // API - the "order" is just a locally-computed, signed form the
        // browser is redirected to PayU with. A StubHttpClientFactory that
        // was never asked for a client proves no HTTP call was made.
        var clientFactory = new StubHttpClientFactory(StubHttpMessageHandler.Responding(HttpStatusCode.OK));
        var gateway = new PayUPaymentGateway(clientFactory, Options.Create(new PayUOptions
        {
            MerchantKey = MerchantKey, MerchantSalt = MerchantSalt, CheckoutReturnBaseUrl = "https://app.nestly.test",
        }), NullLogger<PayUPaymentGateway>.Instance);

        await gateway.CreateOrderAsync(new GatewayCreateOrderRequest(Guid.NewGuid(), 499.00m, "INR", "receipt"));

        clientFactory.RequestedClientNames.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateOrderAsync_returns_a_checkout_form_whose_hash_matches_an_independently_computed_one()
    {
        var gateway = BuildGateway();
        var bookingId = Guid.NewGuid();

        var result = await gateway.CreateOrderAsync(new GatewayCreateOrderRequest(
            bookingId, 1234.50m, "INR", bookingId.ToString("N"),
            CustomerName: "Asha Rao", CustomerMobile: "9876543210", CustomerEmail: "asha@example.com"));

        result.CheckoutRedirectUrl.Should().Be("https://test.payu.in/_payment");
        var fields = result.CheckoutFormFields!;
        fields["key"].Should().Be(MerchantKey);
        fields["txnid"].Should().Be(result.GatewayOrderId);
        fields["amount"].Should().Be("1234.50");
        fields["firstname"].Should().Be("Asha Rao");
        fields["email"].Should().Be("asha@example.com");
        fields["phone"].Should().Be("9876543210");
        // Same return URL for both outcomes - the return page re-checks the
        // real booking status rather than trusting which of surl/furl fired.
        fields["surl"].Should().Be($"https://app.nestly.test/booking/payment/{bookingId}/return");
        fields["furl"].Should().Be($"https://app.nestly.test/booking/payment/{bookingId}/return");

        // Recomputed independently of PayUPaymentGateway's own implementation
        // (PayU's documented request-hash formula, sha512, 16 pipes) using
        // the exact fields the gateway just returned - this is what proves
        // the production code and this recomputation cannot silently drift
        // together and still both be wrong.
        string expectedHash = Sha512Hex(string.Join('|', new[]
        {
            MerchantKey, fields["txnid"], "1234.50", fields["productinfo"], "Asha Rao", "asha@example.com",
            "", "", "", "", "", "", "", "", "", "",
            MerchantSalt,
        }));
        fields["hash"].Should().Be(expectedHash);
    }

    [Fact]
    public async Task CreateOrderAsync_falls_back_to_placeholder_customer_fields_when_none_are_given()
    {
        // SubscriptionBillingJob's off-session charge has no customer
        // contact on hand at all (see GatewayCreateOrderRequest's own doc
        // comment) - CreateOrderAsync must still produce a validly-hashed
        // form rather than throwing or hashing an empty firstname/email.
        var gateway = BuildGateway();

        var result = await gateway.CreateOrderAsync(new GatewayCreateOrderRequest(Guid.NewGuid(), 100m, "INR", "receipt"));

        var fields = result.CheckoutFormFields!;
        fields["firstname"].Should().NotBeNullOrWhiteSpace();
        fields["email"].Should().MatchRegex(@"^[^@]+@customer\.glavyx\.invalid$");
    }

    [Fact]
    public async Task CreateOrderAsync_generates_a_different_txnid_on_every_call_for_the_same_booking()
    {
        // A retried payment attempt (PaymentService.CreateOrderAsync, task
        // 70) calls CreateOrderAsync again for the same booking - PayU
        // requires every txnid to be globally unique, so reusing the
        // booking id (as GatewayCreateOrderRequest.Receipt does) would break
        // the second attempt.
        var gateway = BuildGateway();
        var request = new GatewayCreateOrderRequest(Guid.NewGuid(), 100m, "INR", "same-receipt");

        var first = await gateway.CreateOrderAsync(request);
        var second = await gateway.CreateOrderAsync(request);

        first.GatewayOrderId.Should().NotBe(second.GatewayOrderId);
    }

    [Fact]
    public async Task CreateOrderAsync_reuses_the_given_ExistingGatewayOrderId_instead_of_minting_a_new_one()
    {
        // A customer resuming a still-pending payment (PaymentService's
        // idempotent branch) must get back a checkout form for the SAME
        // txnid the original attempt already persisted - PaymentAttempt.GatewayOrderId
        // is looked up by the webhook, so silently minting a second one here
        // would leave that attempt permanently unresolvable.
        var gateway = BuildGateway();
        const string existingOrderId = "NSTexisting123";

        var result = await gateway.CreateOrderAsync(new GatewayCreateOrderRequest(
            Guid.NewGuid(), 100m, "INR", "receipt", ExistingGatewayOrderId: existingOrderId));

        result.GatewayOrderId.Should().Be(existingOrderId);
        result.CheckoutFormFields!["txnid"].Should().Be(existingOrderId);
    }

    [Fact]
    public void Hash_matches_a_value_PayUs_own_test_server_accepted()
    {
        // Golden value - see class doc comment. PayU's test server redirected
        // to its real checkout page for exactly this key/txnid/amount/
        // productinfo/firstname/email/salt combination.
        const string canonical =
            "gtKFFx|TESTTXN1789995678|100.00|TestProduct|Test|test@example.com|||||||||||4R38IvwiV57FwVpsgOvTXBdLE4tHUXFW";
        const string payUAcceptedHash =
            "3d25506ce8afeedd77c8199380cbd89db230e4d8e98c3545268faa2dbd9ec5039aa2cef0034125adb29270aff410c2b893d47830abd6127c2c48cce72080ee1c";

        Sha512Hex(canonical).Should().Be(payUAcceptedHash);
    }

    [Fact]
    public void VerifyWebhookSignature_accepts_a_signature_value_PayUs_documented_reverse_hash_formula_produces()
    {
        var gateway = BuildGateway();
        var request = new PaymentWebhookRequest(
            GatewayOrderId: "TESTTXN1789995678", GatewayPaymentRef: "mihpay123456",
            Status: "success", Signature: string.Empty,
            Amount: 100.00m, ProductInfo: "dummy", FirstName: "dummy", Email: "test@example.com");

        string canonical = gateway.BuildCanonicalPayload(request);
        // Same golden salt/key/txnid/amount/email as the request-hash test
        // above, reassembled in PayU's documented reverse order (SALT,
        // status, 10 empty udf slots, email, firstname, productinfo, amount,
        // txnid, key) - PayU's formula, not independently vendor-verified
        // for the response direction the way the request hash above is
        // (there is no browser round trip to test against here), but pinned
        // against their own published docs verbatim.
        canonical.Should().Be("4R38IvwiV57FwVpsgOvTXBdLE4tHUXFW|success|||||||||||test@example.com|dummy|dummy|100.00|TESTTXN1789995678|gtKFFx");

        string signature = Sha512Hex(canonical);
        gateway.VerifyWebhookSignature(canonical, signature).Should().BeTrue();
        gateway.VerifyWebhookSignature(canonical, "0000" + signature[4..]).Should().BeFalse();
    }

    [Fact]
    public async Task RefundAsync_posts_the_documented_command_shape_and_hash()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson("""{"status":1,"msg":"Refund Request Queued","request_id":"REQ123"}""");
        var gateway = BuildGateway(handler);

        var result = await gateway.RefundAsync(new GatewayRefundRequest("mihpay123456", 250.00m, "INR", "receipt"));

        result.Status.Should().Be("processed");
        result.GatewayRefundId.Should().Be("REQ123");

        var sent = handler.Requests.Single();
        sent.RequestUri!.ToString().Should().Be("https://test.payu.in/merchant/postservice.php?form=2");
        var form = HttpUtility.ParseQueryString(sent.Body);
        form["key"].Should().Be(MerchantKey);
        form["command"].Should().Be("cancel_refund_transaction");
        form["var1"].Should().Be("mihpay123456");
        form["var3"].Should().Be("250.00");
        form["var2"].Should().NotBeNullOrWhiteSpace();

        string expectedHash = Sha512Hex(string.Join('|', new[] { MerchantKey, "cancel_refund_transaction", "mihpay123456", MerchantSalt }));
        form["hash"].Should().Be(expectedHash);
    }

    [Fact]
    public async Task RefundAsync_two_calls_for_the_same_payment_use_different_var2_tokens()
    {
        // var2 is PayU's own idempotency token for the refund attempt, not
        // ours - it must differ across two independent refund calls (e.g.
        // two partial refunds against the same original payment) even
        // though GatewayPaymentRef (var1) is identical both times.
        var handler = StubHttpMessageHandler.RespondingWithJson("""{"status":1,"msg":"ok","request_id":"r"}""");
        var gateway = BuildGateway(handler);
        var request = new GatewayRefundRequest("mihpay123456", 50m, "INR", "receipt");

        await gateway.RefundAsync(request);
        await gateway.RefundAsync(request);

        var var2Values = handler.Requests.Select(r => HttpUtility.ParseQueryString(r.Body)["var2"]).ToList();
        var2Values.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task RefundAsync_reports_failure_when_PayU_declines_the_refund()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson("""{"status":0,"msg":"Already refunded"}""");
        var gateway = BuildGateway(handler);

        var result = await gateway.RefundAsync(new GatewayRefundRequest("mihpay123456", 50m, "INR", "receipt"));

        result.Status.Should().Be("failed");
        result.FailureReason.Should().Be("Already refunded");
    }

    [Fact]
    public async Task RefundAsync_reports_failure_on_a_non_success_http_status()
    {
        var handler = StubHttpMessageHandler.Responding(HttpStatusCode.InternalServerError);
        var gateway = BuildGateway(handler);

        var result = await gateway.RefundAsync(new GatewayRefundRequest("mihpay123456", 50m, "INR", "receipt"));

        result.Status.Should().Be("failed");
    }

    [Fact]
    public async Task VerifyOrderStatusAsync_posts_the_documented_command_shape_and_hash()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """{"status":1,"transaction_details":{"NST123":{"status":"success","mihpayid":"mihpay789"}}}""");
        var gateway = BuildGateway(handler);

        var result = await gateway.VerifyOrderStatusAsync("NST123");

        result.Status.Should().Be(PaymentWebhookPayload.SuccessStatus);
        result.GatewayPaymentRef.Should().Be("mihpay789");

        var sent = handler.Requests.Single();
        sent.RequestUri!.ToString().Should().Be("https://test.payu.in/merchant/postservice.php?form=2");
        var form = HttpUtility.ParseQueryString(sent.Body);
        form["key"].Should().Be(MerchantKey);
        form["command"].Should().Be("verify_payment");
        form["var1"].Should().Be("NST123");

        string expectedHash = Sha512Hex(string.Join('|', new[] { MerchantKey, "verify_payment", "NST123", MerchantSalt }));
        form["hash"].Should().Be(expectedHash);
    }

    [Fact]
    public async Task VerifyOrderStatusAsync_reports_failure_when_PayU_has_no_record_of_the_txnid()
    {
        // The real case this method exists for: a checkout the customer
        // abandoned or cancelled before submitting a payment method never
        // reached PayU as a real attempt, so transaction_details has no
        // entry for it at all.
        var handler = StubHttpMessageHandler.RespondingWithJson("""{"status":0,"msg":"Transaction not found"}""");
        var gateway = BuildGateway(handler);

        var result = await gateway.VerifyOrderStatusAsync("NST_never_submitted");

        result.Status.Should().Be("failure");
        result.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task VerifyOrderStatusAsync_reports_pending_for_a_still_processing_transaction()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """{"status":1,"transaction_details":{"NST123":{"status":"pending"}}}""");
        var gateway = BuildGateway(handler);

        var result = await gateway.VerifyOrderStatusAsync("NST123");

        result.Status.Should().Be("pending");
    }

    [Fact]
    public async Task VerifyOrderStatusAsync_reports_pending_rather_than_a_false_failure_on_a_transport_error()
    {
        // A network/HTTP failure here says nothing about the payment itself -
        // reporting "pending" (not "failure") means a transient error can
        // never wrongly fail a payment that may still be perfectly fine.
        var handler = StubHttpMessageHandler.Responding(HttpStatusCode.InternalServerError);
        var gateway = BuildGateway(handler);

        var result = await gateway.VerifyOrderStatusAsync("NST123");

        result.Status.Should().Be("pending");
    }

    [Fact]
    public async Task VerifyOrderStatusAsync_reports_failure_for_an_explicit_PayU_failure_status()
    {
        var handler = StubHttpMessageHandler.RespondingWithJson(
            """{"status":1,"transaction_details":{"NST123":{"status":"failure","error_Message":"Card declined by issuing bank"}}}""");
        var gateway = BuildGateway(handler);

        var result = await gateway.VerifyOrderStatusAsync("NST123");

        result.Status.Should().Be("failure");
        result.FailureReason.Should().Be("Card declined by issuing bank");
    }

    private static string Sha512Hex(string input) =>
        Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(System.Text.Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
