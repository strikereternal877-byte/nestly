using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Notifications;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.ProviderManagement;
using Nestly.Application.RecurringBookings;
using Nestly.Application.Serviceability;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// The prepaid recurring plan end to end against a real database: the customer
/// places a booking, buys a plan with it, and every visit of the plan is paid
/// for in one checkout. The properties worth pinning are the ones a payment
/// system cannot get wrong - one gateway order covers exactly the visits that
/// were kept, each visit still settles as its own booking (commission, escrow,
/// refunds stay per visit), the outcome lands on all of them or none, and an
/// unpaid purchase releases everything it held.
/// </summary>
public sealed class PrepaidRecurringPlanTests : IClassFixture<TestDatabase>
{
    private const decimal ServicePrice = 500m;

    private readonly TestDatabase _db;

    public PrepaidRecurringPlanTests(TestDatabase db) => _db = db;

    private sealed record Fixture(Customer Customer, CustomerAddress Address, City City, Locality Locality, Service Service, SlotWindow Window);

    /// <summary>Selective stand-in for the eligibility gate: everything is eligible except the bookings named in <see cref="Banned"/>.</summary>
    private sealed class SelectiveEligibilityStub : IEligibleProviderSearchService
    {
        public HashSet<Guid> Banned { get; } = [];

        public async IAsyncEnumerable<ProviderMatchCandidate> FindEligibleAsync(
            Guid bookingId, IReadOnlyCollection<Guid>? excludeProviderIds = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (!Banned.Contains(bookingId))
            {
                yield return new ProviderMatchCandidate(Guid.NewGuid(), null);
            }
        }
    }

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    /// <summary>A customer with a serviceable address and a slot window bookable on every weekday except <paramref name="closedDays"/>.</summary>
    private Fixture Seed(NestlyDbContext context, params DayOfWeek[] closedDays)
    {
        var pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Priya Nair", CustomerStatus.Active);
        var address = new CustomerAddress(
            Guid.NewGuid(), customer.Id, "Home", "12 MG Road", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Priya Nair", "9876543210", true);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        address.LinkToGeography(pincode.Id, locality.Id);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", ServicePrice);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));

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
        foreach (var day in Enum.GetValues<DayOfWeek>().Where(d => !closedDays.Contains(d)))
        {
            context.SlotWindowRules.Add(new SlotWindowRule(Guid.NewGuid(), window.Id, day));
        }

        context.SaveChanges();
        return new Fixture(customer, address, city, locality, service, window);
    }

    private static BookingSummaryService BuildSummaryService(NestlyDbContext context) => new(
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
            new CityPricingPolicyRepository(context), new ServiceVariantRepository(context), new ServiceAddOnGroupRepository(context), new InMemoryCacheService()),
        new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System),
        new SubscriptionBenefitService(new CustomerSubscriptionRepository(context)),
        new WalletService(new WalletLedgerRepository(context), context),
        new ServiceabilityRepository(context),
        TestServices.BookingOptions());

    private static BookingService BuildBookingService(NestlyDbContext context) => new(
        BuildSummaryService(context),
        new BookingRepository(context),
        new CustomerRepository(context),
        new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System),
        TestServices.SlotAvailability(context),
        new NoOpMetricsService(),
        new BookingProviderAssignmentRepository(context),
        new ProviderRepository(context),
        new ReviewRepository(context),
        new CustomerSubscriptionRepository(context),
        new WalletService(new WalletLedgerRepository(context), context),
        new AlwaysEligibleProviderSearchStub(),
        context);

    private static ProviderAssignmentEligibilityService BuildEligibilityService(NestlyDbContext context) => new(
        new BookingRepository(context),
        new ProviderAvailabilityWindowRepository(context),
        new ProviderBlackoutDateRepository(context),
        new ProviderCapacityRepository(context),
        new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        TravelFeasibilityFactory.Sandbox(context),
        context);

    private static RecurringBookingSchedulerService BuildScheduler(NestlyDbContext context, RecurringBookingOptions options)
    {
        var notificationDispatchService = new NotificationDispatchService(
            new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())),
            new NoOpNotificationProvider(),
            new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
            new NotificationEventRepository(context),
            new DeviceTokenRepository(context),
            new CustomerRepository(context),
            new ProviderRepository(context),
            new NoOpMetricsService(),
            NullLogger<NotificationDispatchService>.Instance);

        return new RecurringBookingSchedulerService(
            new RecurringBookingPlanRepository(context),
            new RecurringBookingOccurrenceRepository(context),
            BuildBookingService(context),
            new CustomerRepository(context),
            new ServiceRepository(context),
            new SlotWindowRepository(context),
            new DeviceTokenRepository(context),
            notificationDispatchService,
            new RecurringPlanProviderContinuityService(new BookingRepository(context)),
            BuildEligibilityService(context),
            new EligibleProviderSearchService(
                new ProviderMatchingService(
                    new BookingRepository(context),
                    context,
                    new SandboxRouteEstimateProvider(Options.Create(new SandboxRouteEstimateOptions())),
                    Options.Create(new AutoAssignmentOptions())),
                BuildEligibilityService(context)),
            new WalletService(new WalletLedgerRepository(context), context),
            new NotificationEventRepository(context),
            Options.Create(options),
            NullLogger<RecurringBookingSchedulerService>.Instance);
    }

    private static RecurringBookingPlanService BuildPlanService(NestlyDbContext context, SandboxPaymentGateway gateway, RecurringBookingOptions options) => new(
        new RecurringBookingPlanRepository(context),
        new RecurringBookingOccurrenceRepository(context),
        BuildSummaryService(context),
        new ServiceRepository(context),
        new BookingRepository(context),
        BuildScheduler(context, options),
        TestServices.CancellationService(context, gateway, TimeProvider.System),
        Options.Create(options),
        TestServices.Clock(),
        TestServices.Policies(),
        new SlotWindowRepository(context),
        new RecurringPlanNotifier(
            new CustomerRepository(context),
            new ServiceRepository(context),
            new SlotWindowRepository(context),
            new DeviceTokenRepository(context),
            BuildDispatchService(context),
            NullLogger<RecurringPlanNotifier>.Instance),
        NullLogger<RecurringBookingPlanService>.Instance);

    private static NotificationDispatchService BuildDispatchService(NestlyDbContext context) => new(
        new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())),
        new NoOpNotificationProvider(),
        new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
        new NotificationEventRepository(context),
        new DeviceTokenRepository(context),
        new CustomerRepository(context),
        new ProviderRepository(context),
        new NoOpMetricsService(),
        NullLogger<NotificationDispatchService>.Instance);

    private static RecurringPlanOccurrenceReleaseHandler BuildReleaseHandler(NestlyDbContext context) => new(
        new BookingRepository(context),
        new RecurringBookingPlanRepository(context),
        BuildReleaseService(context),
        new CustomerRepository(context),
        new ServiceRepository(context),
        new DeviceTokenRepository(context),
        BuildDispatchService(context),
        Options.Create(new RecurringBookingOptions()),
        NullLogger<RecurringPlanOccurrenceReleaseHandler>.Instance);

    private static UnpaidBookingReleaseService BuildReleaseService(NestlyDbContext context) => new(
        new BookingRepository(context),
        TestServices.SlotAvailability(context),
        new WalletService(new WalletLedgerRepository(context), context),
        new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System));

    private static PaymentWebhookService BuildWebhookService(NestlyDbContext context, SandboxPaymentGateway gateway) => new(
        new PaymentTransactionRepository(context),
        new PaymentGroupRepository(context),
        new RecurringBookingPlanRepository(context),
        new BookingRepository(context),
        new ServiceRepository(context),
        gateway,
        new CommissionService(Options.Create(new CommissionOptions())),
        new EscrowService(new PlatformEscrowLedgerRepository(context)),
        context,
        new NoOpMetricsService(),
        NullLogger<PaymentWebhookService>.Instance);

    private static PaymentService BuildPaymentService(NestlyDbContext context, SandboxPaymentGateway gateway, IEligibleProviderSearchService? search = null) => new(
        new PaymentTransactionRepository(context),
        new BookingRepository(context),
        gateway,
        gateway,
        BuildWebhookService(context, gateway),
        search ?? new AlwaysEligibleProviderSearchStub(),
        new PaymentGroupRepository(context),
        new RecurringBookingPlanRepository(context),
        new RecurringBookingOccurrenceRepository(context),
        BuildReleaseService(context));

    private sealed class NoOpNotificationProvider : INotificationProvider
    {
        public Task<BuildingBlocks.Results.Result> SendSmsAsync(string toMobile, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildingBlocks.Results.Result.Success());

        public Task<BuildingBlocks.Results.Result> SendEmailAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) =>
            Task.FromResult(BuildingBlocks.Results.Result.Success());
    }

    private static DateOnly LeadDate => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));

    /// <summary>The booking the customer has just placed and is about to buy a plan with.</summary>
    private async Task<Guid> PlaceLeadAsync(Fixture fixture)
    {
        using var context = _db.CreateContext();
        var request = new BookingSummaryRequest(
            fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, fixture.Window.Id, LeadDate, Quantity: 1, []);
        var created = await BuildBookingService(context).CreateAsync(fixture.Customer.Id, request);
        created.IsSuccess.Should().BeTrue();
        return created.Value.Id;
    }

    private async Task<RecurringBookingPlanResponse> BuyPlanAsync(
        Fixture fixture, Guid leadBookingId, RecurringBookingRecurrenceFrequency frequency, int? repeats, RecurringBookingOptions? options = null)
    {
        using var context = _db.CreateContext();
        var request = new CreateRecurringBookingPlanRequest(
            fixture.Service.Id, fixture.City.Id, fixture.Address.Id, fixture.Locality.Id, fixture.Window.Id, Quantity: 1,
            frequency,
            RecurrenceDayOfWeek: null, RecurrenceDayOfMonth: null,
            StartDate: LeadDate.AddDays(1), EndDate: null, OccurrenceCount: repeats, AddOns: [],
            PrepaidUpfront: true, LeadBookingId: leadBookingId);

        var result = await BuildPlanService(context, BuildGateway(), options ?? new RecurringBookingOptions())
            .CreateAsync(fixture.Customer.Id, request);
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : string.Empty);
        return result.Value;
    }

    private async Task<PaymentOrderResponse> CreateOrderAsync(Fixture fixture, Guid leadBookingId, IEligibleProviderSearchService? search = null)
    {
        using var context = _db.CreateContext();
        var result = await BuildPaymentService(context, BuildGateway(), search)
            .CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(leadBookingId, null));
        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : string.Empty);
        return result.Value;
    }

    private async Task DeliverCallbackAsync(string gatewayOrderId, string status)
    {
        var gateway = BuildGateway();
        using var context = _db.CreateContext();
        string payload = PaymentWebhookPayload.Build(gatewayOrderId, "sandbox_pay_ref", status);
        var result = await BuildWebhookService(context, gateway).HandleCallbackAsync(
            new PaymentWebhookRequest(gatewayOrderId, "sandbox_pay_ref", status, gateway.SignPayload(payload)));
        result.IsSuccess.Should().BeTrue();
    }

    private async Task<IReadOnlyList<Booking>> PlanBookingsAsync(Guid planId)
    {
        using var context = _db.CreateContext();
        return await new BookingRepository(context).ListByRecurringPlanAsync(planId);
    }

    // ---- Purchase -------------------------------------------------------------------------

    [Fact]
    public async Task Buying_a_bounded_daily_plan_creates_every_visit_up_front_unpaid()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);

        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 4);

        plan.PrepaidUpfront.Should().BeTrue();
        plan.PendingPrepaymentBookingId.Should().Be(leadId);
        plan.SkippedDates.Should().BeEmpty();

        var bookings = await PlanBookingsAsync(plan.Id);
        bookings.Should().HaveCount(4);
        bookings.Should().OnlyContain(b => b.Status == BookingStatus.PaymentPending);
        bookings.Select(b => b.SlotDate).Should().BeEquivalentTo(Enumerable.Range(1, 4).Select(i => LeadDate.AddDays(i)));
    }

    [Fact]
    public async Task A_date_that_cannot_be_booked_is_reported_and_left_out_not_made_up_later()
    {
        Fixture fixture;
        var closed = LeadDate.AddDays(3).DayOfWeek; // the third repeat
        using (var context = _db.CreateContext()) { fixture = Seed(context, closed); }
        var leadId = await PlaceLeadAsync(fixture);

        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 4);

        plan.SkippedDates.Should().Equal(LeadDate.AddDays(3));
        var bookings = await PlanBookingsAsync(plan.Id);
        bookings.Should().HaveCount(3, "the skipped date is dropped from the purchase, not replaced by a later one");
        bookings.Max(b => b.SlotDate).Should().Be(LeadDate.AddDays(4));
    }

    [Fact]
    public async Task A_plan_bought_prepaid_is_never_touched_by_the_daily_scheduler()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: null);

        var before = (await PlanBookingsAsync(plan.Id)).Count;

        using (var context = _db.CreateContext())
        {
            await BuildScheduler(context, new RecurringBookingOptions { LeadTimeDays = 30 }).ProcessDueOccurrencesAsync(CancellationToken.None);
        }

        (await PlanBookingsAsync(plan.Id)).Should().HaveCount(before);
    }

    [Fact]
    public async Task An_open_ended_plan_buys_one_cycle_and_no_more()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);

        var plan = await BuyPlanAsync(
            fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: null,
            new RecurringBookingOptions { PrepaidCycleDays = 7 });

        // The lead is day 1 of the 7-day cycle, so the plan supplies days 2..7.
        (await PlanBookingsAsync(plan.Id)).Should().HaveCount(6);
        plan.PrepaidThroughDate.Should().BeNull("coverage only advances once the cycle is paid for");
    }

    [Fact]
    public async Task Buying_the_same_plan_twice_returns_the_first_one()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);

        var first = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 3);
        var second = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 3);

        second.Id.Should().Be(first.Id);
        (await PlanBookingsAsync(first.Id)).Should().HaveCount(3);
    }

    // ---- One checkout ------------------------------------------------------------------------

    [Fact]
    public async Task The_lead_bookings_order_covers_every_visit_and_charges_their_sum_once()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 4);

        var order = await CreateOrderAsync(fixture, leadId);

        order.VisitCount.Should().Be(5, "the lead plus four repeats");
        var bookings = await PlanBookingsAsync(plan.Id);
        using var context2 = _db.CreateContext();
        var lead = await new BookingRepository(context2).GetByIdAsync(leadId);
        order.Amount.Should().Be(lead!.TotalPayableSnapshot + bookings.Sum(b => b.TotalPayableSnapshot));

        var members = await new PaymentGroupRepository(context2).ListMemberTransactionsAsync(
            (await new PaymentGroupRepository(context2).GetByGatewayOrderIdAsync(order.GatewayOrderId))!.Id);
        members.Should().HaveCount(5);
        members.Select(t => t.Amount).Sum().Should().Be(order.Amount);
        members.SelectMany(t => t.Attempts).Should().OnlyContain(a => a.PaymentGroupId != null);
        members.SelectMany(t => t.Attempts).Select(a => a.GatewayOrderId).Distinct().Should().HaveCount(5);
    }

    [Fact]
    public async Task Asking_for_the_order_again_returns_the_one_already_in_flight()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 3);

        var first = await CreateOrderAsync(fixture, leadId);
        var second = await CreateOrderAsync(fixture, leadId);

        second.GatewayOrderId.Should().Be(first.GatewayOrderId);
        second.Amount.Should().Be(first.Amount);
    }

    [Fact]
    public async Task A_visit_nobody_can_serve_is_dropped_from_the_order_and_reported()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 3);

        var bookings = await PlanBookingsAsync(plan.Id);
        var unstaffable = bookings.OrderBy(b => b.SlotDate).Skip(1).First();
        var search = new SelectiveEligibilityStub();
        search.Banned.Add(unstaffable.Id);

        var order = await CreateOrderAsync(fixture, leadId, search);

        order.VisitCount.Should().Be(3, "the lead plus the two visits that can be served");
        order.SkippedDates.Should().Equal(unstaffable.SlotDate);
        using var context2 = _db.CreateContext();
        (await new BookingRepository(context2).GetByIdAsync(unstaffable.Id))!.Status.Should().Be(BookingStatus.Expired);
    }

    [Fact]
    public async Task A_lead_nobody_can_serve_blocks_the_whole_purchase()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 2);

        var search = new SelectiveEligibilityStub();
        search.Banned.Add(leadId);

        using var context2 = _db.CreateContext();
        var result = await BuildPaymentService(context2, BuildGateway(), search)
            .CreateOrderAsync(fixture.Customer.Id, new CreatePaymentOrderRequest(leadId, null));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Payment.NoProviderAvailable");
    }

    // ---- Settlement ---------------------------------------------------------------------------

    [Fact]
    public async Task A_successful_payment_confirms_every_visit_settles_each_on_its_own_and_releases_the_plan()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 4);
        var order = await CreateOrderAsync(fixture, leadId);

        using (var context = _db.CreateContext())
        {
            var simulated = await BuildPaymentService(context, BuildGateway())
                .SimulateAsync(fixture.Customer.Id, new SimulatePaymentRequest(order.GatewayOrderId));
            simulated.IsSuccess.Should().BeTrue();
        }

        using var assertContext = _db.CreateContext();
        var bookingRepository = new BookingRepository(assertContext);
        var visitIds = (await bookingRepository.ListByRecurringPlanAsync(plan.Id)).Select(b => b.Id).Append(leadId).ToList();
        visitIds.Should().HaveCount(5);
        foreach (var id in visitIds)
        {
            (await bookingRepository.GetByIdAsync(id))!.Status.Should().Be(BookingStatus.Confirmed);
            var transaction = await new PaymentTransactionRepository(assertContext).GetByBookingIdAsync(id);
            transaction!.Status.Should().Be(PaymentTransactionStatus.Success);
            transaction.CommissionAmount.Should().NotBeNull("commission is recorded per visit");
        }

        var reloadedPlan = await new RecurringBookingPlanRepository(assertContext).GetByIdAsync(plan.Id);
        reloadedPlan!.IsAwaitingPrepayment.Should().BeFalse();
        reloadedPlan.PrepaidCyclesPaid.Should().Be(1);

        var group = await new PaymentGroupRepository(assertContext).GetByGatewayOrderIdAsync(order.GatewayOrderId);
        group!.Status.Should().Be(PaymentGroupStatus.Success);
    }

    [Fact]
    public async Task A_repeated_success_callback_settles_nothing_twice()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 2);
        var order = await CreateOrderAsync(fixture, leadId);

        await DeliverCallbackAsync(order.GatewayOrderId, PaymentWebhookPayload.SuccessStatus);
        await DeliverCallbackAsync(order.GatewayOrderId, PaymentWebhookPayload.SuccessStatus);
        await DeliverCallbackAsync(order.GatewayOrderId, PaymentWebhookPayload.FailedStatus);

        using var context2 = _db.CreateContext();
        var group = await new PaymentGroupRepository(context2).GetByGatewayOrderIdAsync(order.GatewayOrderId);
        group!.Status.Should().Be(PaymentGroupStatus.Success, "the first resolution wins");
        var members = await new PaymentGroupRepository(context2).ListMemberTransactionsAsync(group.Id);
        members.Should().OnlyContain(t => t.Status == PaymentTransactionStatus.Success);
    }

    [Fact]
    public async Task A_failed_payment_fails_every_visit_together_and_a_retry_opens_a_new_order()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 3);
        var first = await CreateOrderAsync(fixture, leadId);

        await DeliverCallbackAsync(first.GatewayOrderId, PaymentWebhookPayload.FailedStatus);

        using (var context = _db.CreateContext())
        {
            var repository = new BookingRepository(context);
            var ids = (await repository.ListByRecurringPlanAsync(plan.Id)).Select(b => b.Id).Append(leadId);
            foreach (var id in ids)
            {
                (await repository.GetByIdAsync(id))!.Status.Should().Be(BookingStatus.PaymentFailed);
            }

            (await new RecurringBookingPlanRepository(context).GetByIdAsync(plan.Id))!.IsAwaitingPrepayment
                .Should().BeTrue("the cycle is still unpaid");
        }

        var retry = await CreateOrderAsync(fixture, leadId);
        retry.GatewayOrderId.Should().NotBe(first.GatewayOrderId);
        retry.VisitCount.Should().Be(4);
        retry.Amount.Should().Be(first.Amount);

        await DeliverCallbackAsync(retry.GatewayOrderId, PaymentWebhookPayload.SuccessStatus);

        using var assertContext = _db.CreateContext();
        var bookings = new BookingRepository(assertContext);
        foreach (var id in (await bookings.ListByRecurringPlanAsync(plan.Id)).Select(b => b.Id).Append(leadId))
        {
            (await bookings.GetByIdAsync(id))!.Status.Should().Be(BookingStatus.Confirmed);
        }
    }

    // ---- Abandonment -----------------------------------------------------------------------------

    [Fact]
    public async Task An_unpaid_first_purchase_is_abandoned_with_everything_it_held()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 3);

        // The lead's payment window lapses - the expiry sweep expires it and the resulting event reaches the handler.
        using (var context = _db.CreateContext())
        {
            var lead = (await new BookingRepository(context).GetByIdAsync(leadId))!;
            await BuildReleaseService(context).ExpireAsync(lead, "Payment window lapsed.");

            var handler = BuildReleaseHandler(context);
            await handler.Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(leadId, BookingStatus.PaymentPending, BookingStatus.Expired)),
                CancellationToken.None);
        }

        using var assertContext = _db.CreateContext();
        var reloaded = await new RecurringBookingPlanRepository(assertContext).GetByIdAsync(plan.Id);
        reloaded!.Status.Should().Be(RecurringBookingPlanStatus.Cancelled);
        reloaded.IsAwaitingPrepayment.Should().BeFalse();
        (await new BookingRepository(assertContext).ListByRecurringPlanAsync(plan.Id))
            .Should().OnlyContain(b => b.Status == BookingStatus.Expired, "the rest of the unpaid cycle must stop holding slots");
    }

    // ---- Cancellation and refunds -----------------------------------------------------------------

    [Fact]
    public async Task Cancelling_a_paid_plan_cancels_its_visits_and_refunds_them()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 3);
        var order = await CreateOrderAsync(fixture, leadId);
        await DeliverCallbackAsync(order.GatewayOrderId, PaymentWebhookPayload.SuccessStatus);

        using (var context = _db.CreateContext())
        {
            var cancelled = await BuildPlanService(context, BuildGateway(), new RecurringBookingOptions())
                .CancelAsync(fixture.Customer.Id, plan.Id);
            cancelled.IsSuccess.Should().BeTrue(cancelled.IsFailure ? cancelled.Error.Message : string.Empty);
        }

        using var assertContext = _db.CreateContext();
        var bookings = new BookingRepository(assertContext);
        var repeats = await bookings.ListByRecurringPlanAsync(plan.Id);
        repeats.Should().HaveCount(3);
        repeats.Should().OnlyContain(b => b.Status != BookingStatus.Confirmed, "each repeat is cancelled (and refunded) through the ordinary cancellation flow");

        var refunds = new RefundTransactionRepository(assertContext);
        foreach (var repeat in repeats)
        {
            (await refunds.ListByBookingAsync(repeat.Id)).Should().NotBeEmpty($"visit {repeat.SlotDate} was paid, so cancelling it owes a refund");
        }

        (await bookings.GetByIdAsync(leadId))!.Status.Should().Be(BookingStatus.Confirmed, "the booking placed with the plan is its own visit and is not cancelled with the plan");
    }

    // ---- Renewal --------------------------------------------------------------------------------

    [Fact]
    public async Task An_open_ended_plan_asks_for_the_next_cycle_before_the_paid_one_runs_out()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        var options = new RecurringBookingOptions { PrepaidCycleDays = 7, PrepaidRenewalLeadDays = 14 };
        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: null, options);
        var order = await CreateOrderAsync(fixture, leadId);
        await DeliverCallbackAsync(order.GatewayOrderId, PaymentWebhookPayload.SuccessStatus);

        using (var context = _db.CreateContext())
        {
            await BuildScheduler(context, options).ProcessPrepaidRenewalsAsync(CancellationToken.None);
        }

        using var assertContext = _db.CreateContext();
        var reloaded = await new RecurringBookingPlanRepository(assertContext).GetByIdAsync(plan.Id);
        reloaded!.IsAwaitingPrepayment.Should().BeTrue("the next cycle is created and waits for payment");
        reloaded.PrepaidThroughDate.Should().Be(LeadDate.AddDays(6), "coverage does not move until the renewal is paid");

        var renewalLead = await new BookingRepository(assertContext).GetByIdAsync(reloaded.PendingPrepaymentLeadBookingId!.Value);
        renewalLead!.SlotDate.Should().Be(LeadDate.AddDays(7));
        renewalLead.Status.Should().Be(BookingStatus.PaymentPending);

        // A second run does not stack another cycle on top of the unpaid one.
        using (var context = _db.CreateContext())
        {
            await BuildScheduler(context, options).ProcessPrepaidRenewalsAsync(CancellationToken.None);
        }

        using var recheck = _db.CreateContext();
        (await new RecurringBookingPlanRepository(recheck).GetByIdAsync(plan.Id))!.PendingPrepaymentLeadBookingId
            .Should().Be(reloaded.PendingPrepaymentLeadBookingId);
    }

    [Fact]
    public async Task Paying_the_renewal_extends_the_coverage_and_leaving_it_unpaid_pauses_the_plan()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        var options = new RecurringBookingOptions { PrepaidCycleDays = 7, PrepaidRenewalLeadDays = 14 };
        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: null, options);
        await DeliverCallbackAsync((await CreateOrderAsync(fixture, leadId)).GatewayOrderId, PaymentWebhookPayload.SuccessStatus);
        using (var context = _db.CreateContext())
        {
            await BuildScheduler(context, options).ProcessPrepaidRenewalsAsync(CancellationToken.None);
        }

        Guid renewalLeadId;
        using (var context = _db.CreateContext())
        {
            renewalLeadId = (await new RecurringBookingPlanRepository(context).GetByIdAsync(plan.Id))!.PendingPrepaymentLeadBookingId!.Value;
        }

        // Unpaid renewal: the lead's window lapses -> the plan pauses instead of ending.
        using (var context = _db.CreateContext())
        {
            var renewalLead = (await new BookingRepository(context).GetByIdAsync(renewalLeadId))!;
            await BuildReleaseService(context).ExpireAsync(renewalLead, "Payment window lapsed.");
            await BuildReleaseHandler(context)
                .Handle(
                    new DomainEventNotification<BookingStatusChangedEvent>(
                        new BookingStatusChangedEvent(renewalLeadId, BookingStatus.PaymentPending, BookingStatus.Expired)),
                    CancellationToken.None);
        }

        using var assertContext = _db.CreateContext();
        var paused = await new RecurringBookingPlanRepository(assertContext).GetByIdAsync(plan.Id);
        paused!.Status.Should().Be(RecurringBookingPlanStatus.Paused);
        paused.IsAwaitingPrepayment.Should().BeFalse();
        paused.PrepaidThroughDate.Should().Be(LeadDate.AddDays(6), "the unpaid cycle never counted as coverage");
    }

    // ---- One confirmation, not N -----------------------------------------------------------------------

    [Fact]
    public async Task Only_the_lead_visit_sends_confirmation_messages_for_a_prepaid_checkout()
    {
        Fixture fixture;
        using (var context = _db.CreateContext()) { fixture = Seed(context); }
        var leadId = await PlaceLeadAsync(fixture);
        var plan = await BuyPlanAsync(fixture, leadId, RecurringBookingRecurrenceFrequency.Daily, repeats: 3);
        var order = await CreateOrderAsync(fixture, leadId);
        await DeliverCallbackAsync(order.GatewayOrderId, PaymentWebhookPayload.SuccessStatus);

        var memberIds = (await PlanBookingsAsync(plan.Id)).Select(b => b.Id).Append(leadId).ToList();
        using (var context = _db.CreateContext())
        {
            var handler = new BookingNotificationTriggerHandler(
                new BookingRepository(context),
                new PaymentTransactionRepository(context),
                new PaymentGroupRepository(context),
                new BookingCancellationRepository(context),
                new RefundTransactionRepository(context),
                new ProviderRepository(context),
                new NotificationDispatchService(
                    new NotificationTemplateRenderer(new FakeNotificationTemplateRepository(), new MemoryCache(new MemoryCacheOptions())),
                    new NoOpNotificationProvider(),
                    new SandboxPushNotificationProvider(NullLogger<SandboxPushNotificationProvider>.Instance),
                    new NotificationEventRepository(context),
                    new DeviceTokenRepository(context),
                    new CustomerRepository(context),
                    new ProviderRepository(context),
                    new NoOpMetricsService(),
                    NullLogger<NotificationDispatchService>.Instance),
                TestServices.IntentCoordinator(context),
                TestServices.FulfilmentNotifications(),
                NullLogger<BookingNotificationTriggerHandler>.Instance);

            foreach (var id in memberIds)
            {
                await handler.HandleAsync(new BookingStatusChangedEvent(id, BookingStatus.PaymentPending, BookingStatus.Confirmed));
            }
        }

        using var assertContext = _db.CreateContext();
        var sent = await new NotificationEventRepository(assertContext).ListByCustomerAsync(fixture.Customer.Id);
        sent.Where(n => n.EventType == NotificationEventType.BookingConfirmed).Should().HaveCount(1);
        sent.Where(n => n.EventType == NotificationEventType.PaymentSuccess).Should().HaveCount(1);
        sent.Single(n => n.EventType == NotificationEventType.BookingConfirmed).BookingId.Should().Be(leadId);
    }
}
