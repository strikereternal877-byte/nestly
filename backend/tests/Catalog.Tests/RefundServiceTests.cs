using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Refunds;
using Nestly.Application.Serviceability;
using Nestly.Application.Wallet;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers tasks 75a-d: refund entity/types, full refund, partial refund calculation + policy rules, and status lifecycle.</summary>
public sealed class RefundServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public RefundServiceTests(TestDatabase db) => _db = db;

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    private static BookingService BuildBookingService(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var couponService = new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System);
        var summaryService = new BookingSummaryService(
            new ServiceRepository(context),
            new ServiceAddOnRepository(context),
            new ServiceGroupRepository(context),
            new CustomerAddressRepository(context),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TestServices.Clock()),
            new PriceCalculationService(
                new ServiceRepository(context),
                new ServiceAddOnRepository(context),
                new ServiceabilityRepository(context),
                new ServiceCityPriceRepository(context),
                new CityPricingPolicyRepository(context), new ServiceVariantRepository(context), new ServiceAddOnGroupRepository(context), new InMemoryCacheService()),
            couponService,
            new SubscriptionBenefitService(new CustomerSubscriptionRepository(context)),
            new WalletService(new WalletLedgerRepository(context), context),
        new ServiceabilityRepository(context),
        TestServices.BookingOptions());

        return new BookingService(
            summaryService,
            new BookingRepository(context),
            new CustomerRepository(context),
            couponService,
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TestServices.Clock()),
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new ProviderRepository(context),
            new ReviewRepository(context),
            new CustomerSubscriptionRepository(context),
            new WalletService(new WalletLedgerRepository(context), context),
            new AlwaysEligibleProviderSearchStub(),
            context);
    }

    private static RefundService BuildRefundService(Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            new BookingRepository(context),
            new PaymentTransactionRepository(context),
            new RefundTransactionRepository(context),
            new WalletService(new WalletLedgerRepository(context), context),
            new EscrowService(new PlatformEscrowLedgerRepository(context)),
            gateway,
            context);

    private static PaymentWebhookService BuildWebhookService(
        IPaymentTransactionRepository paymentRepository, IBookingRepository bookingRepository,
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            paymentRepository, bookingRepository, new ServiceRepository(context), gateway,
            new CommissionService(Options.Create(new CommissionOptions())), new EscrowService(new PlatformEscrowLedgerRepository(context)),
            context, new NoOpMetricsService(), NullLogger<PaymentWebhookService>.Instance);

    private sealed record Fixture(Customer Customer, Guid BookingId, decimal Total);

    private async Task<Fixture> SeedBookingAsync(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, decimal servicePrice = 1000m, decimal walletCreditToApply = 0m,
        bool withFullDiscountCoupon = false)
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var address = new CustomerAddress(
            Guid.NewGuid(), customer.Id, "Home", "221B Baker Street", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210", true);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        address.LinkToGeography(pincode.Id, locality.Id);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", servicePrice);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, futureDate.DayOfWeek);

        context.Add(customer);
        context.Add(address);
        context.States.Add(state);
        context.Cities.Add(city);
        context.Zones.Add(zone);
        context.Pincodes.Add(pincode);
        context.Localities.Add(locality);
        context.Add(category);
        context.Add(service);
        context.ServicePincodeMappings.Add(new ServicePincodeMapping(Guid.NewGuid(), service.Id, pincode.Id));
        context.SlotWindows.Add(window);
        context.SlotWindowRules.Add(rule);

        string? couponCode = null;
        if (withFullDiscountCoupon)
        {
            couponCode = "FREE" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
            context.Add(new Coupon(
                Guid.NewGuid(), couponCode, "Fully discounted", CouponDiscountType.Percentage, 100m,
                maxDiscountAmount: null, minOrderAmount: 0m,
                DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30),
                usageLimitTotal: null, usageLimitPerCustomer: null,
                applicableCategoryId: null, CouponCustomerSegment.All));
        }

        context.SaveChanges();

        if (walletCreditToApply > 0)
        {
            await new WalletService(new WalletLedgerRepository(context), context)
                .CreditAsync(customer.Id, walletCreditToApply, WalletSourceType.PromotionalCredit, null, "Test wallet credit");
        }

        var request = new BookingSummaryRequest(
            service.Id, city.Id, address.Id, locality.Id, window.Id, futureDate, Quantity: 1, [],
            CouponCode: couponCode,
            ApplyWalletCredit: walletCreditToApply > 0);
        var created = await BuildBookingService(context).CreateAsync(customer.Id, request);
        created.IsSuccess.Should().BeTrue();

        return new Fixture(customer, created.Value.Id, created.Value.Price.TotalPayable);
    }

    /// <summary>Drives a fresh booking through payment success and cancellation, leaving it eligible for refund (Confirmed -> CancelledByCustomer).</summary>
    private async Task<Fixture> SeedCancelledPaidBookingAsync(IPaymentGateway gateway, decimal servicePrice = 1000m, decimal walletCreditToApply = 0m)
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext, servicePrice, walletCreditToApply);
        }

        string gatewayOrderId;
        using (var orderContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(orderContext);
            var bookingRepository = new BookingRepository(orderContext);
            var paymentService = new PaymentService(
                paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway,
                BuildWebhookService(paymentRepository, bookingRepository, orderContext, gateway),
                new AlwaysEligibleProviderSearchStub());
            var order = await paymentService.CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(fixture.BookingId, null));
            gatewayOrderId = order.Value.GatewayOrderId;
        }

        using (var callbackContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(callbackContext);
            var bookingRepository = new BookingRepository(callbackContext);
            var webhookService = BuildWebhookService(paymentRepository, bookingRepository, callbackContext, gateway);
            string payload = PaymentWebhookPayload.Build(gatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus);
            string signature = gateway.SignPayload(payload);
            var callback = await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus, signature));
            callback.IsSuccess.Should().BeTrue();
        }

        using (var cancelContext = _db.CreateContext())
        {
            var bookingRepository = new BookingRepository(cancelContext);
            var booking = await bookingRepository.GetByIdAsync(fixture.BookingId);
            booking!.TransitionTo(BookingStatus.CancelledByCustomer, "Customer changed their mind.");
            await bookingRepository.UpdateAsync(booking);
        }

        return fixture;
    }

    /// <summary>
    /// Task 331 + 356: a booking with nothing left to pay is Confirmed on
    /// creation and never gets a PaymentTransaction at all. Cancelled here so
    /// it is refund-eligible, exactly as <see cref="SeedCancelledPaidBookingAsync"/>
    /// leaves a gateway-paid one - minus the payment round trip, which for
    /// this booking never happened.
    /// </summary>
    private async Task<Fixture> SeedCancelledZeroPayableBookingAsync(
        decimal servicePrice = 1000m, decimal walletCreditToApply = 0m, bool withFullDiscountCoupon = false)
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext, servicePrice, walletCreditToApply, withFullDiscountCoupon);
            fixture.Total.Should().Be(0m, "this fixture exists to cover bookings that were never charged anything");
        }

        using (var cancelContext = _db.CreateContext())
        {
            var bookingRepository = new BookingRepository(cancelContext);
            var booking = await bookingRepository.GetByIdAsync(fixture.BookingId);
            booking!.Status.Should().Be(BookingStatus.Confirmed, "task 331 confirms a zero-payable booking without a payment step");
            booking.TransitionTo(BookingStatus.CancelledByCustomer, "Customer changed their mind.");
            await bookingRepository.UpdateAsync(booking);
        }

        return fixture;
    }

    [Fact]
    public async Task InitiateFullRefundAsync_refunds_the_full_amount_via_gateway_and_marks_the_booking_Refunded()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1001m); // avoid the .13 paisa failure convention

        using var context = _db.CreateContext();
        var result = await BuildRefundService(context, gateway).InitiateFullRefundAsync(fixture.BookingId, "Customer cancellation");

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalAmount.Should().Be(fixture.Total);
        result.Value.Settlements.Should().ContainSingle("a booking funded only by the gateway settles in one place");
        result.Value.Primary.FundingSource.Should().Be(RefundFundingSource.Payment);
        result.Value.Primary.PaymentTransactionId.Should().NotBeNull();
        result.Value.Primary.Type.Should().Be(RefundType.Full);
        result.Value.Primary.Method.Should().Be(RefundMethod.Gateway);
        result.Value.Primary.Status.Should().Be(RefundStatus.Refunded);
        result.Value.Primary.Amount.Should().Be(fixture.Total);
        result.Value.Primary.GatewayRefundRef.Should().NotBeNullOrEmpty();

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking!.Status.Should().Be(BookingStatus.Refunded);
    }

    [Fact]
    public async Task InitiateFullRefundAsync_via_wallet_credits_the_customers_wallet()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1002m);

        using (var context = _db.CreateContext())
        {
            var result = await BuildRefundService(context, gateway).InitiateFullRefundAsync(fixture.BookingId, "Goodwill wallet refund", RefundMethod.Wallet);
            result.IsSuccess.Should().BeTrue();
            result.Value.Primary.Method.Should().Be(RefundMethod.Wallet);
            result.Value.Primary.FundingSource.Should().Be(
                RefundFundingSource.Payment, "the money came from the gateway even though it is being handed back as wallet credit");
            result.Value.Primary.GatewayRefundRef.Should().BeNull("a wallet-settled refund never calls the gateway");
        }

        using var readContext = _db.CreateContext();
        var balance = await new WalletService(new WalletLedgerRepository(readContext), readContext).GetBalanceAsync(fixture.Customer.Id);
        balance.Value.Balance.Should().Be(fixture.Total);
    }

    /// <summary>
    /// Task 310, remodelled by task 356: a booking that spent wallet balance
    /// at checkout gets it back when the booking is refunded - the customer
    /// never received the service that balance paid for. Deliberately
    /// different from a Coupon's redemption, which is never reversed on any
    /// refund (see CouponService - RedemptionCount has no decrement path
    /// anywhere). The reversal is now a wallet-FUNDED RefundTransaction of
    /// its own rather than an invisible ledger credit, so the customer's
    /// refund history adds up to what they actually got back.
    /// </summary>
    [Fact]
    public async Task InitiateFullRefundAsync_reverses_the_wallet_credit_the_booking_applied_at_checkout()
    {
        var gateway = BuildGateway();
        // 1000 total, 300 paid from wallet at checkout - the gateway payment
        // (and therefore the refund) only ever covers the remaining 700.
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1000m, walletCreditToApply: 300m);
        fixture.Total.Should().Be(700m, "the booking's own snapshot must already be net of the wallet credit applied at checkout");

        using (var preRefundContext = _db.CreateContext())
        {
            var balanceBeforeRefund = await new WalletService(new WalletLedgerRepository(preRefundContext), preRefundContext)
                .GetBalanceAsync(fixture.Customer.Id);
            balanceBeforeRefund.Value.Balance.Should().Be(0m, "checkout should have fully drawn down the 300 that was applied");
        }

        using (var context = _db.CreateContext())
        {
            var result = await BuildRefundService(context, gateway).InitiateFullRefundAsync(fixture.BookingId, "Customer cancellation");
            result.IsSuccess.Should().BeTrue();
            result.Value.TotalAmount.Should().Be(1000m, "the customer funded the booking with 700 of gateway money and 300 of wallet balance");
            result.Value.Settlements.Should().HaveCount(2);

            var paymentSettlement = result.Value.Settlements.Single(s => s.FundingSource == RefundFundingSource.Payment);
            paymentSettlement.Amount.Should().Be(700m);
            paymentSettlement.Method.Should().Be(RefundMethod.Gateway);
            paymentSettlement.PaymentTransactionId.Should().NotBeNull();
            result.Value.Primary.Should().Be(paymentSettlement, "the payment-funded settlement is the one a single-reference caller records");

            var walletSettlement = result.Value.Settlements.Single(s => s.FundingSource == RefundFundingSource.Wallet);
            walletSettlement.Amount.Should().Be(300m);
            walletSettlement.Method.Should().Be(RefundMethod.Wallet, "wallet-funded money never went through a gateway that could take it back");
            walletSettlement.PaymentTransactionId.Should().BeNull();
            walletSettlement.Status.Should().Be(RefundStatus.Refunded);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking!.Status.Should().Be(BookingStatus.Refunded);

        var balanceAfterRefund = await new WalletService(new WalletLedgerRepository(readContext), readContext).GetBalanceAsync(fixture.Customer.Id);
        balanceAfterRefund.Value.Balance.Should().Be(300m, "the wallet credit spent on this booking must be handed back once it's fully refunded");
    }

    [Fact]
    public async Task Two_partial_refunds_that_sum_to_the_full_amount_move_the_booking_to_Refunded_only_on_the_second()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1000m);
        decimal half = fixture.Total / 2;

        using (var firstContext = _db.CreateContext())
        {
            var first = await BuildRefundService(firstContext, gateway).InitiatePartialRefundAsync(fixture.BookingId, half, "Partial refund 1");
            first.IsSuccess.Should().BeTrue();
            first.Value.Primary.Type.Should().Be(RefundType.Partial);
            first.Value.Primary.Status.Should().Be(RefundStatus.Refunded);
        }

        using (var midContext = _db.CreateContext())
        {
            var booking = await new BookingRepository(midContext).GetByIdAsync(fixture.BookingId);
            booking!.Status.Should().Be(BookingStatus.RefundPending, "only part of the payment has been refunded so far");
        }

        using (var secondContext = _db.CreateContext())
        {
            var second = await BuildRefundService(secondContext, gateway).InitiatePartialRefundAsync(fixture.BookingId, half, "Partial refund 2");
            second.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var finalBooking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        finalBooking!.Status.Should().Be(BookingStatus.Refunded);

        var refunds = await new RefundTransactionRepository(readContext).ListByBookingAsync(fixture.BookingId);
        refunds.Should().HaveCount(2);
        refunds.Sum(r => r.Amount).Should().Be(fixture.Total);
    }

    [Fact]
    public async Task InitiatePartialRefundAsync_rejects_an_amount_exceeding_the_remaining_refundable_balance()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1000m);

        using var context = _db.CreateContext();
        var result = await BuildRefundService(context, gateway).InitiatePartialRefundAsync(fixture.BookingId, fixture.Total + 1, "Too much");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Refund.ExceedsRemainingBalance");
    }

    [Fact]
    public async Task InitiateFullRefundAsync_rejects_a_booking_that_is_not_eligible()
    {
        Fixture fixture;
        using (var seedContext = _db.CreateContext())
        {
            fixture = await SeedBookingAsync(seedContext);
        }

        var gateway = BuildGateway();
        using var context = _db.CreateContext();
        // Still PaymentPending - never paid, never cancelled/completed.
        var result = await BuildRefundService(context, gateway).InitiateFullRefundAsync(fixture.BookingId, "Not eligible");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Refund.BookingNotEligible");
    }

    [Fact]
    public async Task InitiateFullRefundAsync_rejects_a_second_attempt_once_the_booking_is_already_fully_Refunded()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1003m);

        using (var firstContext = _db.CreateContext())
        {
            var first = await BuildRefundService(firstContext, gateway).InitiateFullRefundAsync(fixture.BookingId, "First refund");
            first.IsSuccess.Should().BeTrue();
        }

        using var secondContext = _db.CreateContext();
        var second = await BuildRefundService(secondContext, gateway).InitiateFullRefundAsync(fixture.BookingId, "Duplicate refund attempt");

        // A fully-refunded booking moves to the terminal Refunded status, which
        // the eligibility gate (not the balance check) correctly rejects -
        // Refunded has no outgoing transitions in BookingLifecycle, so a
        // second refund is blocked before "how much is left" is ever asked.
        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be("Refund.BookingNotEligible");
    }

    /// <summary>
    /// Task 356, the gap this task exists to close: a booking whose wallet
    /// balance covered the entire price has no PaymentTransaction (task 331
    /// confirms it without one), so before this change every refund attempt
    /// bailed with Refund.NoSuccessfulPayment and the money was simply gone.
    /// </summary>
    [Fact]
    public async Task InitiateFullRefundAsync_refunds_a_fully_wallet_covered_booking_back_to_the_wallet()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledZeroPayableBookingAsync(servicePrice: 1000m, walletCreditToApply: 1500m);

        IReadOnlyList<WalletLedgerEntryResponse> ledgerBeforeRefund;
        using (var preRefundContext = _db.CreateContext())
        {
            var wallet = new WalletService(new WalletLedgerRepository(preRefundContext), preRefundContext);
            (await wallet.GetBalanceAsync(fixture.Customer.Id)).Value.Balance
                .Should().Be(500m, "checkout drew the whole 1000 price out of the 1500 balance");
            ledgerBeforeRefund = (await wallet.GetLedgerAsync(fixture.Customer.Id)).Value;
        }

        Guid walletRefundId;
        using (var context = _db.CreateContext())
        {
            var result = await BuildRefundService(context, gateway).InitiateFullRefundAsync(fixture.BookingId, "Customer cancellation");

            result.IsSuccess.Should().BeTrue();
            result.Value.TotalAmount.Should().Be(1000m);
            result.Value.Settlements.Should().ContainSingle("nothing but wallet balance funded this booking");
            result.Value.Primary.FundingSource.Should().Be(RefundFundingSource.Wallet);
            result.Value.Primary.PaymentTransactionId.Should().BeNull("there is no gateway payment this refund could point at");
            result.Value.Primary.Method.Should().Be(RefundMethod.Wallet);
            result.Value.Primary.Status.Should().Be(RefundStatus.Refunded);
            result.Value.Primary.GatewayRefundRef.Should().BeNull();
            walletRefundId = result.Value.Primary.Id;
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId);
        booking!.Status.Should().Be(BookingStatus.Refunded);

        var walletService = new WalletService(new WalletLedgerRepository(readContext), readContext);
        (await walletService.GetBalanceAsync(fixture.Customer.Id)).Value.Balance
            .Should().Be(1500m, "the customer is back where they started - they paid entirely from the wallet and got nothing");

        var ledgerAfterRefund = (await walletService.GetLedgerAsync(fixture.Customer.Id)).Value;
        ledgerAfterRefund.Should().HaveCount(ledgerBeforeRefund.Count + 1, "a refund appends to the ledger, it never rewrites it");
        ledgerAfterRefund.Should().Contain(ledgerBeforeRefund, "SRS 14.5: existing entries are immutable");

        var reversal = ledgerAfterRefund.Except(ledgerBeforeRefund).Single();
        reversal.EntryType.Should().Be(WalletEntryType.Credit);
        reversal.Amount.Should().Be(1000m);
        reversal.BalanceAfter.Should().Be(1500m);
        reversal.SourceType.Should().Be(WalletSourceType.BookingWalletCreditReversal);
        reversal.SourceReferenceId.Should().Be(walletRefundId, "SRS 14.5: every entry references the source event that produced it");
    }

    /// <summary>
    /// Task 356: the other zero-payable producer. Nothing was ever collected -
    /// no gateway payment and no wallet balance - so there is genuinely
    /// nothing to hand back, which must read as a clean business refusal that
    /// leaves the booking and the ledger alone.
    /// </summary>
    [Fact]
    public async Task InitiateFullRefundAsync_on_a_fully_coupon_discounted_booking_has_nothing_to_refund()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledZeroPayableBookingAsync(servicePrice: 1000m, withFullDiscountCoupon: true);

        using (var context = _db.CreateContext())
        {
            var result = await BuildRefundService(context, gateway).InitiateFullRefundAsync(fixture.BookingId, "Customer cancellation");

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("Refund.NoSuccessfulPayment");
        }

        using var readContext = _db.CreateContext();
        (await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId))!.Status
            .Should().Be(BookingStatus.CancelledByCustomer, "a refund that never happened must not move the booking");
        (await new RefundTransactionRepository(readContext).ListByBookingAsync(fixture.BookingId)).Should().BeEmpty();
        (await new WalletService(new WalletLedgerRepository(readContext), readContext).GetLedgerAsync(fixture.Customer.Id))
            .Value.Should().BeEmpty("a coupon discount is the merchant's money, not the customer's - there is nothing to credit back");
    }

    /// <summary>
    /// Task 356: a booking paid part-wallet/part-gateway must refund BOTH
    /// halves. The split is gateway-first, so a cancellation fee is withheld
    /// from the wallet-funded portion last - see RefundAllocationCalculator.
    /// </summary>
    [Fact]
    public async Task InitiatePartialRefundAsync_draws_the_gateway_payment_down_before_the_wallet_credit()
    {
        var gateway = BuildGateway();
        // 1000 total, 300 from the wallet at checkout, 700 through the gateway.
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1000m, walletCreditToApply: 300m);

        using (var firstContext = _db.CreateContext())
        {
            var first = await BuildRefundService(firstContext, gateway).InitiatePartialRefundAsync(fixture.BookingId, 800m, "Cancellation less a 200 fee");

            first.IsSuccess.Should().BeTrue();
            first.Value.TotalAmount.Should().Be(800m);
            first.Value.Settlements.Should().HaveCount(2);
            first.Value.Settlements.Single(s => s.FundingSource == RefundFundingSource.Payment).Amount
                .Should().Be(700m, "the whole gateway payment comes back before the wallet is touched");
            first.Value.Settlements.Single(s => s.FundingSource == RefundFundingSource.Wallet).Amount
                .Should().Be(100m, "only the shortfall the fee left over is taken out of the wallet-funded portion");
        }

        using (var midContext = _db.CreateContext())
        {
            (await new BookingRepository(midContext).GetByIdAsync(fixture.BookingId))!.Status
                .Should().Be(BookingStatus.RefundPending, "200 of the customer's money is still unrefunded");
            (await new WalletService(new WalletLedgerRepository(midContext), midContext).GetBalanceAsync(fixture.Customer.Id))
                .Value.Balance.Should().Be(100m);
        }

        using (var overAskContext = _db.CreateContext())
        {
            var overAsk = await BuildRefundService(overAskContext, gateway).InitiatePartialRefundAsync(fixture.BookingId, 201m, "Too much");
            overAsk.IsFailure.Should().BeTrue();
            overAsk.Error.Code.Should().Be("Refund.ExceedsRemainingBalance", "only the 200 of wallet-funded money still held is refundable");
        }

        using (var secondContext = _db.CreateContext())
        {
            var second = await BuildRefundService(secondContext, gateway).InitiatePartialRefundAsync(fixture.BookingId, 200m, "Fee waived on review");

            second.IsSuccess.Should().BeTrue();
            second.Value.Settlements.Should().ContainSingle();
            second.Value.Primary.FundingSource.Should().Be(RefundFundingSource.Wallet, "the gateway payment was already exhausted");
        }

        using var readContext = _db.CreateContext();
        (await new BookingRepository(readContext).GetByIdAsync(fixture.BookingId))!.Status.Should().Be(BookingStatus.Refunded);
        (await new WalletService(new WalletLedgerRepository(readContext), readContext).GetBalanceAsync(fixture.Customer.Id))
            .Value.Balance.Should().Be(300m, "every rupee of wallet balance the booking consumed is back");

        var refunds = await new RefundTransactionRepository(readContext).ListByBookingAsync(fixture.BookingId);
        refunds.Should().HaveCount(3);
        refunds.Sum(r => r.Amount).Should().Be(1000m, "gateway 700 + wallet 300 - exactly what the customer funded the booking with");
    }

    /// <summary>Task 356: the wallet-only path is no more double-payable than the gateway path - a fully refunded booking is terminal.</summary>
    [Fact]
    public async Task InitiateFullRefundAsync_rejects_a_second_refund_of_a_fully_wallet_covered_booking()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledZeroPayableBookingAsync(servicePrice: 1000m, walletCreditToApply: 1500m);

        using (var firstContext = _db.CreateContext())
        {
            (await BuildRefundService(firstContext, gateway).InitiateFullRefundAsync(fixture.BookingId, "First refund")).IsSuccess.Should().BeTrue();
        }

        using (var secondContext = _db.CreateContext())
        {
            var second = await BuildRefundService(secondContext, gateway).InitiateFullRefundAsync(fixture.BookingId, "Duplicate refund attempt");
            second.IsFailure.Should().BeTrue();
            second.Error.Code.Should().Be("Refund.BookingNotEligible");
        }

        using var readContext = _db.CreateContext();
        (await new RefundTransactionRepository(readContext).ListByBookingAsync(fixture.BookingId)).Should().ContainSingle();
        (await new WalletService(new WalletLedgerRepository(readContext), readContext).GetBalanceAsync(fixture.Customer.Id))
            .Value.Balance.Should().Be(1500m, "the customer must not be paid twice for one booking");
    }

    [Fact]
    public async Task ListByBookingAsync_does_not_return_another_customers_refunds()
    {
        var gateway = BuildGateway();
        var fixture = await SeedCancelledPaidBookingAsync(gateway, servicePrice: 1004m);

        using (var context = _db.CreateContext())
        {
            var refund = await BuildRefundService(context, gateway).InitiateFullRefundAsync(fixture.BookingId, "Refund");
            refund.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var ownerResult = await BuildRefundService(readContext, gateway).ListByBookingAsync(fixture.Customer.Id, fixture.BookingId);
        ownerResult.IsSuccess.Should().BeTrue();
        ownerResult.Value.Should().ContainSingle();

        var strangerResult = await BuildRefundService(readContext, gateway).ListByBookingAsync(Guid.NewGuid(), fixture.BookingId);
        strangerResult.IsFailure.Should().BeTrue();
        strangerResult.Error.Code.Should().Be("Refund.BookingNotFound");
    }
}
