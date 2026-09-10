using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Amc;
using Nestly.Application.Bookings;
using Nestly.Application.Escrow;
using Nestly.Application.Notifications;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Serviceability;
using Nestly.Application.Wallet;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers Phase 20's AMC module (docs/AMC.md, tasks 323-330) at the service
/// level, against a real (SQLite) database: plan catalog CRUD, purchase,
/// "my contracts", cancel, and the redeem -&gt; booking-completion ->
/// entitlement-decrement round trip that is this module's whole point (visit
/// redemption reuses <see cref="BookingService.CreateAsync"/> unchanged, and
/// the entitlement itself is only ever drawn down by
/// <see cref="AmcVisitOnBookingCompletionHandler"/> on completion, never by
/// <see cref="AmcCustomerService.RedeemVisitAsync"/> itself).
/// </summary>
public sealed class AmcServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;
    private readonly DateTime _now = new(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);

    public AmcServiceTests(TestDatabase db) => _db = db;

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, Guid.NewGuid(), IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _fixedNow;
        public FakeTimeProvider(DateTime now) => _fixedNow = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => _fixedNow;
    }

    private static AmcAdminService BuildAdminService(Nestly.Infrastructure.Persistence.NestlyDbContext context, TimeProvider? timeProvider = null) =>
        new(
            new AmcPlanRepository(context),
            new CustomerAmcContractRepository(context),
            new AuditLogWriter(context, new StubAuditContextProvider()),
            context,
            timeProvider ?? TimeProvider.System);

    private static AmcCustomerService BuildCustomerService(
        Nestly.Infrastructure.Persistence.NestlyDbContext context, IBookingService bookingService, TimeProvider? timeProvider = null) =>
        new(
            new AmcPlanRepository(context),
            new CustomerAmcContractRepository(context),
            new AmcServiceVisitRepository(context),
            bookingService,
            context,
            timeProvider ?? TimeProvider.System);

    private static BookingService BuildBookingService(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var couponService = new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System);
        var summaryService = new BookingSummaryService(
            new ServiceRepository(context),
            new ServiceAddOnRepository(context),
            new ServiceGroupRepository(context),
            new CustomerAddressRepository(context),
            TestServices.SlotAvailability(context),
            new PriceCalculationService(
                new ServiceRepository(context),
                new ServiceAddOnRepository(context),
                new ServiceabilityRepository(context),
                new ServiceCityPriceRepository(context),
                new CityPricingPolicyRepository(context), new ServiceVariantRepository(context), new ServiceAddOnGroupRepository(context)),
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
            TestServices.SlotAvailability(context),
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new ProviderRepository(context),
            new ReviewRepository(context),
            new CustomerSubscriptionRepository(context),
            new WalletService(new WalletLedgerRepository(context), context),
            new AlwaysEligibleProviderSearchStub(),
            context);
    }

    private sealed record Fixture(Customer Customer, CustomerAddress Address, City City, Locality Locality, Service Service, SlotWindow Window, DateOnly SlotDate, Category Category);

    private Fixture Seed(Nestly.Infrastructure.Persistence.NestlyDbContext context)
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
        var category = new Category(Guid.NewGuid(), "AC Repair", "ac-repair-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "AC Service", "ac-service-" + Guid.NewGuid(), "desc", 500m);
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

        return new Fixture(customer, address, city, locality, service, window, futureDate, category);
    }

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    /// <summary>
    /// Drives a normally-priced booking's payment order through a successful
    /// sandbox webhook so it lands on <see cref="BookingStatus.Confirmed"/> -
    /// the only status <see cref="BookingLifecycle"/> allows <c>AwaitingFulfilment</c>
    /// to follow. Not valid for a zero-priced AMC redemption booking - see
    /// <see cref="CompleteZeroPricedAmcBookingAsync"/>'s doc comment for why.
    /// </summary>
    private static async Task PayAndConfirmAsync(Nestly.Infrastructure.Persistence.NestlyDbContext context, Guid customerId, Guid bookingId)
    {
        var gateway = BuildGateway();
        var paymentRepository = new PaymentTransactionRepository(context);
        var bookingRepository = new BookingRepository(context);
        var webhookService = new PaymentWebhookService(
            paymentRepository, bookingRepository, new ServiceRepository(context), gateway,
            new CommissionService(Options.Create(new CommissionOptions())),
            new EscrowService(new PlatformEscrowLedgerRepository(context)),
            context, new NoOpMetricsService(), NullLogger<PaymentWebhookService>.Instance);
        var paymentService = new PaymentService(
            paymentRepository, bookingRepository, gateway, (ISandboxPaymentSimulator)gateway, webhookService,
            new AlwaysEligibleProviderSearchStub());

        var order = await paymentService.CreateOrderAsync(customerId, new CreatePaymentOrderRequest(bookingId, null));
        order.IsSuccess.Should().BeTrue();
        string payload = PaymentWebhookPayload.Build(order.Value.GatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus);
        string signature = gateway.SignPayload(payload);
        var callback = await webhookService.HandleCallbackAsync(new PaymentWebhookRequest(order.Value.GatewayOrderId, "sandbox_pay_ref", PaymentWebhookPayload.SuccessStatus, signature));
        callback.IsSuccess.Should().BeTrue();
    }

    private static async Task CompleteBookingAsync(TestDatabase db, Guid customerId, Guid bookingId)
    {
        using (var paymentContext = db.CreateContext())
        {
            await PayAndConfirmAsync(paymentContext, customerId, bookingId);
        }

        using var lifecycleContext = db.CreateContext();
        var bookingRepository = new BookingRepository(lifecycleContext);
        var booking = await bookingRepository.GetByIdAsync(bookingId);
        booking!.TransitionTo(BookingStatus.AwaitingFulfilment);
        booking.TransitionTo(BookingStatus.Assigned);
        booking.TransitionTo(BookingStatus.InProgress);
        booking.TransitionTo(BookingStatus.Completed);
        await bookingRepository.UpdateAsync(booking);
    }

    /// <summary>
    /// Same end state as <see cref="CompleteBookingAsync"/>, but for a
    /// zero-priced AMC redemption booking: there is no payment/webhook step
    /// to drive at all, because task 331 gave a booking with nothing payable
    /// its own confirmation path - <c>BookingService.CreateAsync</c> confirms
    /// it on creation rather than parking it in PaymentPending, where
    /// <see cref="PaymentTransaction"/>'s non-positive-amount guard would have
    /// stranded it forever. So this helper starts from the Confirmed state the
    /// real production path already left the booking in, and only walks the
    /// fulfilment half a provider would drive, to get to the Completed
    /// transition <see cref="AmcVisitOnBookingCompletionHandler"/> reacts to.
    /// </summary>
    private static async Task CompleteZeroPricedAmcBookingAsync(TestDatabase db, Guid bookingId)
    {
        using var lifecycleContext = db.CreateContext();
        var bookingRepository = new BookingRepository(lifecycleContext);
        var booking = await bookingRepository.GetByIdAsync(bookingId);
        booking!.TotalPayableSnapshot.Should().Be(0m, "this helper is only valid for a zero-priced AMC redemption booking");
        booking.Status.Should().Be(BookingStatus.Confirmed, "task 331 confirms a zero-payable booking at creation - no payment step to simulate");
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        booking.TransitionTo(BookingStatus.Assigned);
        booking.TransitionTo(BookingStatus.InProgress);
        booking.TransitionTo(BookingStatus.Completed);
        await bookingRepository.UpdateAsync(booking);
    }

    private static AmcPlan SeedActivePlan(Nestly.Infrastructure.Persistence.NestlyDbContext context, Guid categoryId, int visitsIncluded = 2, int termMonths = 12) =>
        Seed(context, categoryId, visitsIncluded, termMonths);

    private static AmcPlan Seed(Nestly.Infrastructure.Persistence.NestlyDbContext context, Guid categoryId, int visitsIncluded, int termMonths)
    {
        var plan = new AmcPlan(Guid.NewGuid(), categoryId, "AC AMC " + Guid.NewGuid(), "desc", 3499m, termMonths, visitsIncluded);
        context.Add(plan);
        context.SaveChanges();
        return plan;
    }

    // ---- Admin plan CRUD ----

    [Fact]
    public async Task AdminService_create_then_deactivate_then_activate_a_plan_round_trips()
    {
        using var context = _db.CreateContext();
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        context.Add(category);
        context.SaveChanges();

        var service = BuildAdminService(context);
        var created = await service.CreatePlanAsync(new AmcPlanCreateRequest(category.Id, "AC AMC " + Guid.NewGuid(), "desc", 3499m, 12, 2));

        created.IsSuccess.Should().BeTrue();
        created.Value.IsActive.Should().BeTrue();

        var deactivated = await service.DeactivatePlanAsync(created.Value.Id, Guid.NewGuid());
        deactivated.IsSuccess.Should().BeTrue();
        (await service.GetPlanByIdAsync(created.Value.Id)).Value.IsActive.Should().BeFalse();

        var activated = await service.ActivatePlanAsync(created.Value.Id, Guid.NewGuid());
        activated.IsSuccess.Should().BeTrue();
        (await service.GetPlanByIdAsync(created.Value.Id)).Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task AdminService_rejects_a_duplicate_plan_name()
    {
        using var context = _db.CreateContext();
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        context.Add(category);
        context.SaveChanges();

        var service = BuildAdminService(context);
        var name = "AC AMC " + Guid.NewGuid();
        (await service.CreatePlanAsync(new AmcPlanCreateRequest(category.Id, name, "desc", 3499m, 12, 2))).IsSuccess.Should().BeTrue();

        var duplicate = await service.CreatePlanAsync(new AmcPlanCreateRequest(category.Id, name, "desc", 999m, 6, 1));

        duplicate.IsFailure.Should().BeTrue();
        duplicate.Error.Code.Should().Be("AmcPlan.NameAlreadyExists");
    }

    [Fact]
    public async Task AdminService_deactivating_a_plan_does_not_touch_contracts_already_purchased_on_it()
    {
        using var context = _db.CreateContext();
        var fixture = Seed(context);
        var plan = SeedActivePlan(context, fixture.Category.Id, visitsIncluded: 2, termMonths: 12);
        var customerService = BuildCustomerService(context, BuildBookingService(context), new FakeTimeProvider(_now));

        var purchased = await customerService.PurchaseAsync(fixture.Customer.Id, new AmcContractPurchaseRequest(plan.Id, "Hall AC"));
        purchased.IsSuccess.Should().BeTrue();

        var adminService = BuildAdminService(context);
        (await adminService.DeactivatePlanAsync(plan.Id, Guid.NewGuid())).IsSuccess.Should().BeTrue();

        var reloaded = await customerService.GetMyContractAsync(fixture.Customer.Id, purchased.Value.Id);
        reloaded.IsSuccess.Should().BeTrue();
        reloaded.Value.Price.Should().Be(3499m, "the contract's price was snapshotted at purchase time and an admin deactivating the plan must not reprice it");
    }

    // ---- Customer purchase / browse / list / cancel ----

    // NOTE: browsing (IAmcPlanRepository.ListActiveAsync, which orders by
    // Price) is not covered here - SQLite cannot translate ORDER BY over a
    // decimal column, a limitation of this suite's SQLite TestDatabase, not
    // of the repository (Postgres, what production actually runs on, has no
    // such restriction). SubscriptionPlanRepository.ListActiveAsync has the
    // identical OrderBy(p => p.Price) shape and is, for the same reason,
    // never exercised by SubscriptionTests either - see this file's sibling.

    [Fact]
    public async Task CustomerService_purchase_snapshots_plan_terms_onto_the_contract()
    {
        using var context = _db.CreateContext();
        var fixture = Seed(context);
        var plan = SeedActivePlan(context, fixture.Category.Id, visitsIncluded: 3, termMonths: 12);
        var service = BuildCustomerService(context, BuildBookingService(context), new FakeTimeProvider(_now));

        var result = await service.PurchaseAsync(fixture.Customer.Id, new AmcContractPurchaseRequest(plan.Id, "Bedroom AC"));

        result.IsSuccess.Should().BeTrue();
        result.Value.PlanName.Should().Be(plan.Name);
        result.Value.AssetLabel.Should().Be("Bedroom AC");
        result.Value.VisitsRemaining.Should().Be(3);
        result.Value.Status.Should().Be(CustomerAmcContractStatus.Active);
        result.Value.CanRedeemNow.Should().BeTrue();
    }

    [Fact]
    public async Task CustomerService_purchase_fails_for_an_inactive_or_unknown_plan()
    {
        using var context = _db.CreateContext();
        var fixture = Seed(context);
        var plan = new AmcPlan(Guid.NewGuid(), fixture.Category.Id, "Retired plan " + Guid.NewGuid(), "desc", 999m, 6, 1);
        plan.Deactivate(null);
        context.Add(plan);
        context.SaveChanges();

        var service = BuildCustomerService(context, BuildBookingService(context));

        var result = await service.PurchaseAsync(fixture.Customer.Id, new AmcContractPurchaseRequest(plan.Id, "Hall AC"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Amc.PlanNotFound");
    }

    [Fact]
    public async Task CustomerService_cancel_lets_the_customer_purchase_a_fresh_contract()
    {
        using var context = _db.CreateContext();
        var fixture = Seed(context);
        var plan = SeedActivePlan(context, fixture.Category.Id);
        var service = BuildCustomerService(context, BuildBookingService(context), new FakeTimeProvider(_now));

        var purchased = await service.PurchaseAsync(fixture.Customer.Id, new AmcContractPurchaseRequest(plan.Id, "Hall AC"));
        (await service.CancelAsync(fixture.Customer.Id, purchased.Value.Id)).IsSuccess.Should().BeTrue();

        var reloaded = await service.GetMyContractAsync(fixture.Customer.Id, purchased.Value.Id);
        reloaded.Value.Status.Should().Be(CustomerAmcContractStatus.Cancelled);
    }

    [Fact]
    public async Task CustomerService_a_customer_cannot_see_or_cancel_another_customers_contract()
    {
        using var context = _db.CreateContext();
        var fixture = Seed(context);
        var plan = SeedActivePlan(context, fixture.Category.Id);
        var service = BuildCustomerService(context, BuildBookingService(context), new FakeTimeProvider(_now));
        var purchased = await service.PurchaseAsync(fixture.Customer.Id, new AmcContractPurchaseRequest(plan.Id, "Hall AC"));

        var stranger = Guid.NewGuid();
        (await service.GetMyContractAsync(stranger, purchased.Value.Id)).Error.Code.Should().Be("Amc.ContractNotFound");
        (await service.CancelAsync(stranger, purchased.Value.Id)).Error.Code.Should().Be("Amc.ContractNotFound");
    }

    // ---- Redeem -> booking completion -> entitlement drawdown ----

    [Fact]
    public async Task RedeemVisitAsync_creates_a_zero_priced_booking_linked_to_the_contract_without_touching_entitlement_yet()
    {
        using var context = _db.CreateContext();
        var fixture = Seed(context);
        var plan = SeedActivePlan(context, fixture.Category.Id, visitsIncluded: 2, termMonths: 12);
        var customerService = BuildCustomerService(context, BuildBookingService(context), new FakeTimeProvider(_now));
        var purchased = await customerService.PurchaseAsync(fixture.Customer.Id, new AmcContractPurchaseRequest(plan.Id, "Hall AC"));

        var request = new BookingSummaryRequest(fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, fixture.Window.Id, fixture.SlotDate, Quantity: 1, []);
        var redeemed = await customerService.RedeemVisitAsync(fixture.Customer.Id, purchased.Value.Id, request);

        redeemed.IsSuccess.Should().BeTrue();
        redeemed.Value.Price.TotalPayable.Should().Be(0m, "the visit is prepaid via entitlement, not charged again");
        redeemed.Value.Status.Should().Be(
            BookingStatus.Confirmed,
            "task 331: nothing is payable, so the booking is confirmed on creation instead of waiting for a payment that can never be made");

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(redeemed.Value.Id);
        booking!.AmcContractId.Should().Be(purchased.Value.Id);
        booking.Status.Should().Be(BookingStatus.Confirmed);
        booking.StatusHistory.Should().NotContain(
            h => h.ToStatus == BookingStatus.PaymentPending,
            "a redemption booking never awaits a payment");

        var stillUnredeemed = await customerService.GetMyContractAsync(fixture.Customer.Id, purchased.Value.Id);
        stillUnredeemed.Value.VisitsRemaining.Should().Be(2, "entitlement is only drawn down on booking completion, not on redemption/creation");
    }

    /// <summary>
    /// Task 357: a coupon code riding along on the redemption request is
    /// ignored end to end. <c>BookingService.CreateAsync</c> already skipped
    /// <c>ICouponService.ReserveAsync</c> and nulled the booking's coupon
    /// snapshot for an AMC redemption, but <c>CreateRedemptionRecordAsync</c>
    /// had no matching guard - so the customer's per-coupon usage cap was
    /// silently spent on a discount they never received.
    /// </summary>
    [Fact]
    public async Task RedeemVisitAsync_ignores_a_coupon_on_the_request_and_writes_no_redemption_record()
    {
        Fixture fixture;
        Guid contractId;
        Guid bookingId;
        Guid couponId = Guid.NewGuid();
        string couponCode = "AMC" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            var plan = SeedActivePlan(context, fixture.Category.Id, visitsIncluded: 2, termMonths: 12);
            context.Add(new Coupon(
                couponId, couponCode, "Ignored on an AMC redemption", CouponDiscountType.Percentage, 10m,
                maxDiscountAmount: null, minOrderAmount: 0m,
                DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30),
                usageLimitTotal: null, usageLimitPerCustomer: 1,
                applicableCategoryId: null, CouponCustomerSegment.All));
            context.SaveChanges();

            var customerService = BuildCustomerService(context, BuildBookingService(context), new FakeTimeProvider(_now));
            var purchased = await customerService.PurchaseAsync(fixture.Customer.Id, new AmcContractPurchaseRequest(plan.Id, "Hall AC"));
            contractId = purchased.Value.Id;

            var request = new BookingSummaryRequest(
                fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, fixture.Window.Id,
                fixture.SlotDate, Quantity: 1, [], CouponCode: couponCode);
            var redeemed = await customerService.RedeemVisitAsync(fixture.Customer.Id, contractId, request);

            redeemed.IsSuccess.Should().BeTrue();
            redeemed.Value.Price.TotalPayable.Should().Be(0m, "the contract covers the visit - the coupon changes nothing");
            bookingId = redeemed.Value.Id;
        }

        using var readContext = _db.CreateContext();
        (await new CouponRedemptionRepository(readContext).CountByCouponAndCustomerAsync(couponId, fixture.Customer.Id))
            .Should().Be(0, "no reservation was ever taken, so no redemption record may be written against it");

        var booking = await new BookingRepository(readContext).GetByIdAsync(bookingId);
        booking!.CouponCodeSnapshot.Should().BeNull("the coupon was never applied, so it is not part of the booking's record either");
        booking.AmcContractId.Should().Be(contractId);
    }

    [Fact]
    public async Task RedeemVisitAsync_rejects_a_contract_that_has_no_entitlement_or_term_left()
    {
        Fixture fixture;
        Guid contractId;
        using (var context = _db.CreateContext())
        {
            fixture = Seed(context);
            var plan = SeedActivePlan(context, fixture.Category.Id, visitsIncluded: 2, termMonths: 12);
            var customerService = BuildCustomerService(context, BuildBookingService(context), new FakeTimeProvider(_now));
            var purchased = await customerService.PurchaseAsync(fixture.Customer.Id, new AmcContractPurchaseRequest(plan.Id, "Hall AC"));
            contractId = purchased.Value.Id;
        }

        using (var mutateContext = _db.CreateContext())
        {
            var repository = new CustomerAmcContractRepository(mutateContext);
            var contract = await repository.GetByIdAsync(contractId);
            contract!.Cancel(_now);
            await repository.UpdateAsync(contract);
        }

        // A fresh context/service so the redeem attempt reads the just-persisted
        // Cancelled status rather than a stale tracked instance from purchase.
        using var readContext = _db.CreateContext();
        var freshCustomerService = BuildCustomerService(readContext, BuildBookingService(readContext), new FakeTimeProvider(_now));
        var request = new BookingSummaryRequest(fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, fixture.Window.Id, fixture.SlotDate, Quantity: 1, []);
        var redeemed = await freshCustomerService.RedeemVisitAsync(fixture.Customer.Id, contractId, request);

        redeemed.IsFailure.Should().BeTrue();
        redeemed.Error.Code.Should().Be("Amc.CannotRedeem");
    }

    [Fact]
    public async Task Completing_a_redemption_booking_decrements_entitlement_and_records_a_visit_row()
    {
        using var context = _db.CreateContext();
        var fixture = Seed(context);
        var plan = SeedActivePlan(context, fixture.Category.Id, visitsIncluded: 2, termMonths: 12);
        var customerService = BuildCustomerService(context, BuildBookingService(context), new FakeTimeProvider(_now));
        var purchased = await customerService.PurchaseAsync(fixture.Customer.Id, new AmcContractPurchaseRequest(plan.Id, "Hall AC"));

        var request = new BookingSummaryRequest(fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, fixture.Window.Id, fixture.SlotDate, Quantity: 1, []);
        var redeemed = await customerService.RedeemVisitAsync(fixture.Customer.Id, purchased.Value.Id, request);
        redeemed.IsSuccess.Should().BeTrue();
        var bookingId = redeemed.Value.Id;

        await CompleteZeroPricedAmcBookingAsync(_db, bookingId);

        using (var handlerContext = _db.CreateContext())
        {
            var handler = new AmcVisitOnBookingCompletionHandler(
                new BookingRepository(handlerContext),
                new CustomerAmcContractRepository(handlerContext),
                new AmcServiceVisitRepository(handlerContext),
                new FakeTimeProvider(_now.AddDays(1)),
                NullLogger<AmcVisitOnBookingCompletionHandler>.Instance);

            await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(bookingId, BookingStatus.InProgress, BookingStatus.Completed)),
                CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var afterCompletion = await BuildCustomerService(readContext, BuildBookingService(readContext)).GetMyContractAsync(fixture.Customer.Id, purchased.Value.Id);
        afterCompletion.Value.VisitsRemaining.Should().Be(1);
        afterCompletion.Value.Visits.Should().ContainSingle().Which.BookingId.Should().Be(bookingId);
    }

    [Fact]
    public async Task Completing_the_last_redemption_booking_exhausts_the_contract()
    {
        using var context = _db.CreateContext();
        var fixture = Seed(context);
        var plan = SeedActivePlan(context, fixture.Category.Id, visitsIncluded: 1, termMonths: 12);
        var customerService = BuildCustomerService(context, BuildBookingService(context), new FakeTimeProvider(_now));
        var purchased = await customerService.PurchaseAsync(fixture.Customer.Id, new AmcContractPurchaseRequest(plan.Id, "Hall AC"));

        var request = new BookingSummaryRequest(fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, fixture.Window.Id, fixture.SlotDate, Quantity: 1, []);
        var redeemed = await customerService.RedeemVisitAsync(fixture.Customer.Id, purchased.Value.Id, request);
        var bookingId = redeemed.Value.Id;

        await CompleteZeroPricedAmcBookingAsync(_db, bookingId);

        using (var handlerContext = _db.CreateContext())
        {
            var handler = new AmcVisitOnBookingCompletionHandler(
                new BookingRepository(handlerContext),
                new CustomerAmcContractRepository(handlerContext),
                new AmcServiceVisitRepository(handlerContext),
                new FakeTimeProvider(_now.AddDays(1)),
                NullLogger<AmcVisitOnBookingCompletionHandler>.Instance);

            await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(bookingId, BookingStatus.InProgress, BookingStatus.Completed)),
                CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var afterCompletion = await BuildCustomerService(readContext, BuildBookingService(readContext)).GetMyContractAsync(fixture.Customer.Id, purchased.Value.Id);
        afterCompletion.Value.VisitsRemaining.Should().Be(0);
        afterCompletion.Value.Status.Should().Be(CustomerAmcContractStatus.Exhausted);
    }

    [Fact]
    public async Task Completing_an_ordinary_booking_with_no_amc_contract_is_a_no_op_for_the_handler()
    {
        using var context = _db.CreateContext();
        var fixture = Seed(context);
        var bookingService = BuildBookingService(context);
        var request = new BookingSummaryRequest(fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, fixture.Window.Id, fixture.SlotDate, Quantity: 1, []);
        var created = await bookingService.CreateAsync(fixture.Customer.Id, request);
        created.IsSuccess.Should().BeTrue();

        await CompleteBookingAsync(_db, fixture.Customer.Id, created.Value.Id);

        using var handlerContext = _db.CreateContext();
        var handler = new AmcVisitOnBookingCompletionHandler(
            new BookingRepository(handlerContext),
            new CustomerAmcContractRepository(handlerContext),
            new AmcServiceVisitRepository(handlerContext),
            new FakeTimeProvider(_now),
            NullLogger<AmcVisitOnBookingCompletionHandler>.Instance);

        var act = async () => await handler.Handle(
            new DomainEventNotification<BookingStatusChangedEvent>(
                new BookingStatusChangedEvent(created.Value.Id, BookingStatus.InProgress, BookingStatus.Completed)),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ---- Admin renewal report ----

    [Fact]
    public async Task Admin_renewal_report_zero_fills_every_status_and_surfaces_contracts_within_the_horizon()
    {
        using var context = _db.CreateContext();
        var fixture = Seed(context);
        var plan = SeedActivePlan(context, fixture.Category.Id, visitsIncluded: 2, termMonths: 1);
        var customerService = BuildCustomerService(context, BuildBookingService(context), new FakeTimeProvider(_now));
        var purchased = await customerService.PurchaseAsync(fixture.Customer.Id, new AmcContractPurchaseRequest(plan.Id, "Hall AC"));
        purchased.IsSuccess.Should().BeTrue();

        var adminService = BuildAdminService(context, new FakeTimeProvider(_now));
        var report = await adminService.GetRenewalReportAsync(_now, _now.AddDays(45));

        report.IsSuccess.Should().BeTrue();
        // TestDatabase is shared across every test in this class (IClassFixture),
        // so other tests' contracts persist alongside this one - assertions here
        // check "at least" rather than an exact total.
        report.Value.TotalContracts.Should().BeGreaterThanOrEqualTo(1);
        report.Value.ByStatus.Should().HaveCount(Enum.GetValues<CustomerAmcContractStatus>().Length, "every status gets a zero-filled row even with no contracts in it");
        report.Value.ByStatus.Single(s => s.Status == CustomerAmcContractStatus.Active).ContractCount.Should().BeGreaterThanOrEqualTo(1);
        report.Value.ExpiringOrExhaustedContracts.Should().Contain(c => c.Id == purchased.Value.Id);
    }
}
