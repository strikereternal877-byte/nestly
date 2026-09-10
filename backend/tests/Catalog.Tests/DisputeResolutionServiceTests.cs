using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Refunds;
using Nestly.Application.Serviceability;
using Nestly.Application.Support;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Covers task 155: dispute mark/resolve workflow (refund-valid vs. close/rework-invalid) on a support ticket.</summary>
public sealed class DisputeResolutionServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public DisputeResolutionServiceTests(TestDatabase db) => _db = db;

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

    private static PaymentWebhookService BuildWebhookService(
        IPaymentTransactionRepository paymentRepository, IBookingRepository bookingRepository,
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        new(
            paymentRepository, bookingRepository, new ServiceRepository(context), gateway,
            new CommissionService(Options.Create(new CommissionOptions())), new EscrowService(new PlatformEscrowLedgerRepository(context)),
            context, new NoOpMetricsService(), NullLogger<PaymentWebhookService>.Instance);

    private static DisputeResolutionService BuildDisputeService(Nestly.Infrastructure.Persistence.NestlyDbContext context, IPaymentGateway gateway) =>
        BuildDisputeService(context, new RefundService(
            new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
            new WalletService(new WalletLedgerRepository(context), context), new EscrowService(new PlatformEscrowLedgerRepository(context)), gateway, context));

    private static DisputeResolutionService BuildDisputeService(Nestly.Infrastructure.Persistence.NestlyDbContext context, IRefundService refundService) =>
        new(
            new SupportTicketRepository(context),
            refundService,
            new AuditLogWriter(context, new StubAuditContextProvider()));

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, Guid.NewGuid(), IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }

    /// <summary>
    /// NESTLY-003 regression: <see cref="IRefundService"/> stub that fails the
    /// test outright if invoked more than once, so a second
    /// <c>ResolveAsync</c> call on an already-resolved dispute proves it never
    /// reaches the refund path at all - not just that the refund happens to
    /// return a failure the second time.
    /// </summary>
    private sealed class CountingRefundService : IRefundService
    {
        public int CallCount { get; private set; }

        public Task<Result<RefundOutcomeResponse>> InitiateFullRefundAsync(Guid bookingId, string reason, RefundMethod method = RefundMethod.Gateway) =>
            Invoke(bookingId);

        public Task<Result<RefundOutcomeResponse>> InitiatePartialRefundAsync(Guid bookingId, decimal amount, string reason, RefundMethod method = RefundMethod.Gateway) =>
            Invoke(bookingId);

        public Task<Result<IReadOnlyList<RefundTransactionResponse>>> ListByBookingAsync(Guid customerId, Guid bookingId) =>
            throw new NotSupportedException("Not exercised by this stub.");

        private Task<Result<RefundOutcomeResponse>> Invoke(Guid bookingId)
        {
            CallCount++;
            CallCount.Should().Be(1, "a resolved dispute must never reach the refund service again");
            var settlement = new RefundTransactionResponse(
                Guid.NewGuid(), bookingId, Guid.NewGuid(), RefundFundingSource.Payment, RefundType.Full, RefundMethod.Gateway,
                1m, RefundStatus.Refunded, GatewayRefundRef: null, Reason: "test",
                CreatedAtUtc: DateTime.UtcNow, ProcessedAtUtc: DateTime.UtcNow);
            return Task.FromResult(Result.Success(new RefundOutcomeResponse(bookingId, settlement.Amount, [settlement])));
        }
    }

    private sealed record Fixture(Customer Customer, Guid BookingId, decimal Total);

    private async Task<Fixture> SeedPaidBookingAsync(IPaymentGateway gateway, decimal servicePrice)
    {
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        Customer customer;
        Guid bookingId;
        decimal total;

        using (var context = _db.CreateContext())
        {
            customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
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
            context.SaveChanges();

            var request = new BookingSummaryRequest(service.Id, city.Id, address.Id, locality.Id, window.Id, futureDate, Quantity: 1, []);
            var created = await BuildBookingService(context).CreateAsync(customer.Id, request);
            created.IsSuccess.Should().BeTrue();
            bookingId = created.Value.Id;
            total = created.Value.Price.TotalPayable;
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
            var order = await paymentService.CreateOrderAsync(customer.Id, new CreatePaymentOrderRequest(bookingId, null));
            gatewayOrderId = order.Value.GatewayOrderId;
        }

        using (var callbackContext = _db.CreateContext())
        {
            var paymentRepository = new PaymentTransactionRepository(callbackContext);
            var bookingRepository = new BookingRepository(callbackContext);
            var webhookService = BuildWebhookService(paymentRepository, bookingRepository, callbackContext, gateway);
            string payload = PaymentWebhookPayload.Build(gatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus);
            string signature = gateway.SignPayload(payload);
            await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(gatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus, signature));
        }

        return new Fixture(customer, bookingId, total);
    }

    /// <summary>
    /// IRefundService only accepts a refund once a booking has actually
    /// reached Completed/Cancelled/RefundPending (see RefundService's
    /// EligibleBookingStatuses) - a dispute resolved with a refund is
    /// realistically raised against a booking that has already been
    /// through the fulfilment flow, so tests exercising that path advance
    /// the booking here first.
    /// </summary>
    private async Task CompleteBookingAsync(Guid bookingId)
    {
        using var context = _db.CreateContext();
        var repository = new BookingRepository(context);
        var booking = await repository.GetByIdAsync(bookingId);
        booking!.TransitionTo(BookingStatus.AwaitingFulfilment);
        booking.TransitionTo(BookingStatus.Assigned);
        booking.TransitionTo(BookingStatus.InProgress);
        booking.TransitionTo(BookingStatus.Completed);
        await repository.UpdateAsync(booking);
    }

    private async Task<Guid> SeedTicketAsync(Guid customerId, Guid? bookingId)
    {
        using var context = _db.CreateContext();
        var ticket = new SupportTicket(Guid.NewGuid(), customerId, bookingId, SupportTicketCategory.PricingDispute, "Wrong charge", "I was billed twice for this booking.");
        await new SupportTicketRepository(context).AddAsync(ticket);
        return ticket.Id;
    }

    [Fact]
    public async Task MarkDisputedAsync_opens_a_dispute_and_moves_a_new_ticket_into_investigation()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1201m);
        var ticketId = await SeedTicketAsync(fixture.Customer.Id, fixture.BookingId);

        using var context = _db.CreateContext();
        var result = await BuildDisputeService(context, gateway).MarkDisputedAsync(ticketId);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsDisputed.Should().BeTrue();
        result.Value.Status.Should().Be(SupportTicketStatus.InProgress);
    }

    [Fact]
    public async Task ResolveAsync_as_RefundValid_raises_a_full_refund_and_resolves_the_ticket()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1202m);
        await CompleteBookingAsync(fixture.BookingId);
        var ticketId = await SeedTicketAsync(fixture.Customer.Id, fixture.BookingId);

        using (var markContext = _db.CreateContext())
        {
            var mark = await BuildDisputeService(markContext, gateway).MarkDisputedAsync(ticketId);
            mark.IsSuccess.Should().BeTrue();
        }

        using var context = _db.CreateContext();
        var result = await BuildDisputeService(context, gateway).ResolveAsync(
            ticketId, new ResolveDisputeRequest(DisputeResolutionOutcome.RefundValid, "Duplicate charge confirmed - full refund issued.", null));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(SupportTicketStatus.Resolved);
        result.Value.DisputeOutcome.Should().Be(DisputeResolutionOutcome.RefundValid);
        result.Value.RefundTransactionId.Should().NotBeNull();
        result.Value.RefundStatus.Should().Be(RefundStatus.Refunded);

        using var readContext = _db.CreateContext();
        var refunds = await new RefundTransactionRepository(readContext).ListByBookingAsync(fixture.BookingId);
        refunds.Should().ContainSingle(r => r.Amount == fixture.Total);
    }

    [Fact]
    public async Task ResolveAsync_as_RefundValid_with_a_specific_amount_raises_a_partial_refund()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1203m);
        await CompleteBookingAsync(fixture.BookingId);
        var ticketId = await SeedTicketAsync(fixture.Customer.Id, fixture.BookingId);

        using (var markContext = _db.CreateContext())
        {
            await BuildDisputeService(markContext, gateway).MarkDisputedAsync(ticketId);
        }

        using var context = _db.CreateContext();
        var result = await BuildDisputeService(context, gateway).ResolveAsync(
            ticketId, new ResolveDisputeRequest(DisputeResolutionOutcome.RefundValid, "Partial overcharge refunded.", 200m));

        result.IsSuccess.Should().BeTrue();

        using var readContext = _db.CreateContext();
        var refunds = await new RefundTransactionRepository(readContext).ListByBookingAsync(fixture.BookingId);
        refunds.Should().ContainSingle(r => r.Amount == 200m && r.Type == RefundType.Partial);
    }

    [Fact]
    public async Task ResolveAsync_as_ClosedInvalid_resolves_without_raising_any_refund()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1204m);
        var ticketId = await SeedTicketAsync(fixture.Customer.Id, fixture.BookingId);

        using (var markContext = _db.CreateContext())
        {
            await BuildDisputeService(markContext, gateway).MarkDisputedAsync(ticketId);
        }

        using var context = _db.CreateContext();
        var result = await BuildDisputeService(context, gateway).ResolveAsync(
            ticketId, new ResolveDisputeRequest(DisputeResolutionOutcome.ClosedInvalid, "Charge verified correct - no duplicate found.", null));

        result.IsSuccess.Should().BeTrue();
        result.Value.DisputeOutcome.Should().Be(DisputeResolutionOutcome.ClosedInvalid);
        result.Value.RefundTransactionId.Should().BeNull();

        using var readContext = _db.CreateContext();
        var refunds = await new RefundTransactionRepository(readContext).ListByBookingAsync(fixture.BookingId);
        refunds.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_without_MarkDisputed_first_is_rejected()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1205m);
        var ticketId = await SeedTicketAsync(fixture.Customer.Id, fixture.BookingId);

        using var context = _db.CreateContext();
        var result = await BuildDisputeService(context, gateway).ResolveAsync(
            ticketId, new ResolveDisputeRequest(DisputeResolutionOutcome.ClosedInvalid, "n/a", null));

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Dispute.NotOpen");
    }

    [Fact]
    public async Task ResolveAsync_as_RefundValid_on_a_ticket_with_no_linked_booking_is_rejected()
    {
        Guid customerId = Guid.NewGuid();
        using (var context = _db.CreateContext())
        {
            context.Add(new Customer(customerId, "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active));
            context.SaveChanges();
        }
        var ticketId = await SeedTicketAsync(customerId, null);

        var gateway = BuildGateway();
        using (var markContext = _db.CreateContext())
        {
            await BuildDisputeService(markContext, gateway).MarkDisputedAsync(ticketId);
        }

        using var context2 = _db.CreateContext();
        var result = await BuildDisputeService(context2, gateway).ResolveAsync(
            ticketId, new ResolveDisputeRequest(DisputeResolutionOutcome.RefundValid, "n/a", null));

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Dispute.NoLinkedBooking");
    }

    /// <summary>
    /// NESTLY-003 regression: before this fix, ResolveAsync checked only
    /// <c>ticket.IsDisputed</c> (never cleared by a first resolution) before
    /// calling the refund service, so a second resolve attempt raised a real
    /// second refund and only then failed on the ticket's own Resolved ->
    /// Resolved transition - reporting a failure for an operation that had
    /// just moved money. Using <see cref="CountingRefundService"/> instead of
    /// the real <see cref="RefundService"/> proves the second call is
    /// rejected before the refund service is ever reached, not merely that
    /// the eventual outcome happens to look like a rejection.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_a_second_time_never_reaches_the_refund_service_again()
    {
        var gateway = BuildGateway();
        var fixture = await SeedPaidBookingAsync(gateway, 1206m);
        var ticketId = await SeedTicketAsync(fixture.Customer.Id, fixture.BookingId);
        var refundService = new CountingRefundService();

        using (var markContext = _db.CreateContext())
        {
            var mark = await BuildDisputeService(markContext, refundService).MarkDisputedAsync(ticketId);
            mark.IsSuccess.Should().BeTrue();
        }

        using (var firstContext = _db.CreateContext())
        {
            var first = await BuildDisputeService(firstContext, refundService).ResolveAsync(
                ticketId, new ResolveDisputeRequest(DisputeResolutionOutcome.RefundValid, "Duplicate charge confirmed.", null));
            first.IsSuccess.Should().BeTrue();
        }

        refundService.CallCount.Should().Be(1);

        using var secondContext = _db.CreateContext();
        var second = await BuildDisputeService(secondContext, refundService).ResolveAsync(
            ticketId, new ResolveDisputeRequest(DisputeResolutionOutcome.RefundValid, "Attempted re-resolve.", null));

        second.IsSuccess.Should().BeFalse();
        second.Error.Code.Should().Be("Dispute.CannotResolve");
        refundService.CallCount.Should().Be(1, "an already-resolved dispute must never reach the refund service a second time");
    }
}
