using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.BookingManagement;
using Nestly.Application.Bookings;
using Nestly.Application.Cancellations;
using Nestly.Application.Catalog;
using Nestly.Application.Coupons;
using Nestly.Application.Identity;
using Nestly.Application.Payments;
using Nestly.Application.Refunds;
using Nestly.Application.Reschedules;
using Nestly.Application.Serviceability;
using Nestly.Application.Support;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Authorization;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Phase 6 closing QA suite (task 132b): real end-to-end admin workflows
/// exercised through the actual services - no mocks anywhere, the same
/// "construct the real dependency graph over a SQLite <see cref="TestDatabase"/>"
/// approach <see cref="PostBookingQaSuiteTests"/> and <see cref="FinancialQaSuiteTests"/>
/// already use to close out their own phases. Each business-logic detail
/// exercised here (cancellation fee math, reschedule eligibility, ticket
/// status transitions, coupon rule validation, ...) already has its own
/// focused unit-test file; this suite's job is only confirming the admin
/// flow works end to end through the real services once assembled together,
/// per task 132b's four required scenarios:
/// <list type="number">
/// <item>login -&gt; a permission-gated action -&gt; an audit log entry recorded,</item>
/// <item>booking cancel/reschedule/refund through <see cref="BookingManagementService"/>,</item>
/// <item>a support ticket assign -&gt; respond -&gt; escalate -&gt; resolve workflow
/// through <see cref="AdminSupportTicketService"/>, and</item>
/// <item>a CRUD cycle through the catalog (<see cref="ServiceManagementService"/>)
/// and coupon (<see cref="CouponManagementService"/>) admin management services.</item>
/// </list>
/// </summary>
public sealed class AdminWorkflowsQaSuiteTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public AdminWorkflowsQaSuiteTests(TestDatabase db) => _db = db;

    private sealed class NoOpMfaChallengeProvider : IAdminMfaChallengeProvider
    {
        public Task<Result> VerifyAsync(AdminUser adminUser, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class StubAuditContextProvider(AuditActorType actorType, Guid? actorId) : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(actorType, actorId, IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }

    private static AdminLoginService BuildLoginService(NestlyDbContext context) =>
        new(
            new AdminUserRepository(context),
            new AdminTokenService(Options.Create(new AdminJwtOptions
            {
                SigningKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
                Issuer = "Nestly",
                Audience = "Nestly.AdminUsers",
                AccessTokenMinutes = 10
            })),
            new NoOpMfaChallengeProvider(),
            new AdminRolePermissionQueryService(context),
            new AuditLogWriter(context, new StubAuditContextProvider(AuditActorType.Anonymous, null)),
            context,
            Options.Create(new AdminAccountOptions()));

    // ---------------------------------------------------------------
    // 1. Login -> permission-gated action -> audit trail.
    // ---------------------------------------------------------------

    /// <summary>
    /// Seeds a real role/permission grant the way the task 96a seed
    /// migration would (one <see cref="AdminPermission"/> + <see cref="RolePermissionMapping"/>
    /// row per code the static <see cref="AdminPermissionCatalog"/> matrix
    /// grants that role), so <see cref="AdminRolePermissionQueryService"/>
    /// (task 96c, the same lookup login uses to build the JWT's permission
    /// claims) returns real, database-backed codes rather than a hand-typed
    /// stand-in for what the seed would have produced.
    /// </summary>
    private static Guid SeedRoleWithCatalogGrants(NestlyDbContext context, string roleName)
    {
        var role = new AdminRole(Guid.NewGuid(), roleName, roleName);
        context.Add(role);

        foreach (string code in AdminPermissionCatalog.RolePermissionCodes[roleName])
        {
            AdminPermissionDefinition definition = AdminPermissionCatalog.Permissions.Single(p => p.Code == code);
            var permission = new AdminPermission(Guid.NewGuid(), definition.Code, definition.Module, definition.Description);
            context.Add(permission);
            context.Add(new RolePermissionMapping(Guid.NewGuid(), role.Id, permission.Id));
        }

        context.SaveChanges();
        return role.Id;
    }

    [Fact]
    public async Task Login_then_a_permission_gated_write_action_both_write_to_the_audit_trail()
    {
        const string email = "workflow-admin@nestly.test";
        const string password = "correct-horse-battery-staple";

        Guid roleId, adminUserId;
        using (var context = _db.CreateContext())
        {
            roleId = SeedRoleWithCatalogGrants(context, AdminRoleNames.BookingAdmin);

            var adminUser = new AdminUser(Guid.NewGuid(), email, "placeholder", "Workflow Admin");
            adminUser.SetPasswordHash(new PasswordHasher<AdminUser>().HashPassword(adminUser, password));
            adminUser.AssignRole(roleId);
            context.Add(adminUser);
            context.SaveChanges();
            adminUserId = adminUser.Id;
        }

        // Step 1: log in through the real AdminLoginService.
        using (var context = _db.CreateContext())
        {
            var result = await BuildLoginService(context).LoginAsync(new AdminLoginRequest(email, password));
            result.IsSuccess.Should().BeTrue();
        }

        using (var context = _db.CreateContext())
        {
            var loginAudit = await context.Set<AuditLog>().SingleOrDefaultAsync(a =>
                a.EntityName == "AdminUser" && a.EntityId == adminUserId.ToString() && a.Action == "AdminLoginSucceeded");
            loginAudit.Should().NotBeNull("a successful admin login must be audited (SRS 12.1.2, task 95g)");
        }

        // Step 2: the permission claims a real JWT would carry (task 96c)
        // drive a real PermissionAuthorizationHandler check for a write
        // action Booking Admin's grant actually holds.
        using (var context = _db.CreateContext())
        {
            var rolePermissions = await new AdminRolePermissionQueryService(context).GetPermissionsAsync(roleId);
            rolePermissions.PermissionCodes.Should().Contain("bookings.write");

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, adminUserId.ToString()) };
            claims.AddRange(rolePermissions.PermissionCodes.Select(code => new Claim(AdminClaimTypes.Permission, code)));
            var user = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));

            var handler = new PermissionAuthorizationHandler(
                new AuditLogWriter(context, new StubAuditContextProvider(AuditActorType.AdminUser, adminUserId)), context);
            var authContext = new AuthorizationHandlerContext([new PermissionRequirement("bookings.write")], user, resource: null);

            await handler.HandleAsync(authContext);
            authContext.HasSucceeded.Should().BeTrue();
        }

        using (var context = _db.CreateContext())
        {
            var permissionAudit = await context.Set<AuditLog>().SingleOrDefaultAsync(a =>
                a.EntityName == "AdminPermissionCheck" && a.EntityId == adminUserId.ToString() && a.Action == "PermissionGranted:bookings.write");
            permissionAudit.Should().NotBeNull("a granted write-permission check must be audited as a sensitive action");
        }
    }

    // ---------------------------------------------------------------
    // 2. Booking cancel / reschedule / refund through BookingManagementService.
    // ---------------------------------------------------------------

    private static BookingService BuildBookingService(NestlyDbContext context)
    {
        var couponService = new CouponService(new CouponRepository(context), new CouponRedemptionRepository(context), new BookingRepository(context), TimeProvider.System);
        var summaryService = new BookingSummaryService(
            new ServiceRepository(context),
            new ServiceAddOnRepository(context),
            new CustomerAddressRepository(context),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context),
                new SlotBlackoutRepository(context),
                new SlotBookingPolicyRepository(context),
                new SlotCapacityRepository(context),
                TimeProvider.System),
            new PriceCalculationService(
                new ServiceRepository(context),
                new ServiceAddOnRepository(context),
                new ServiceabilityRepository(context),
                new ServiceCityPriceRepository(context),
                new CityPricingPolicyRepository(context)),
            couponService,
            new SubscriptionBenefitService(new CustomerSubscriptionRepository(context)));

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
                TimeProvider.System),
            new NoOpMetricsService(),
            new BookingProviderAssignmentRepository(context),
            new CustomerSubscriptionRepository(context));
    }

    private static BookingManagementService BuildBookingManagementService(NestlyDbContext context) => new(
        new BookingRepository(context),
        new PaymentTransactionRepository(context),
        new BookingCancellationRepository(context),
        new BookingRescheduleRepository(context),
        new RefundTransactionRepository(context),
        new CancellationService(
            new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
            new RefundService(
                new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
                new WalletService(new WalletLedgerRepository(context)), new EscrowService(new PlatformEscrowLedgerRepository(context)),
                BuildGateway(), context),
            new BookingCancellationRepository(context), new BookingProviderAssignmentRepository(context), TimeProvider.System, Options.Create(new CancellationPolicyOptions())),
        new RescheduleService(
            new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
            new SlotAvailabilityService(
                new ServiceabilityRepository(context),
                new ServiceabilityValidationService(new ServiceabilityRepository(context), new InMemoryCacheService()),
                new SlotWindowRepository(context), new SlotBlackoutRepository(context), new SlotBookingPolicyRepository(context), new SlotCapacityRepository(context), TimeProvider.System),
            new BookingRescheduleRepository(context), TimeProvider.System, Options.Create(new ReschedulePolicyOptions())),
        new RefundService(
            new BookingRepository(context), new PaymentTransactionRepository(context), new RefundTransactionRepository(context),
            new WalletService(new WalletLedgerRepository(context)), new EscrowService(new PlatformEscrowLedgerRepository(context)),
            BuildGateway(), context),
        new AuditLogWriter(context, new StubAuditContextProvider(AuditActorType.AdminUser, Guid.NewGuid())),
        context,
        new BookingCompletionProofRepository(context));

    private static SandboxPaymentGateway BuildGateway() =>
        new(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" }));

    private async Task<(Guid CustomerId, Guid BookingId, Guid CityId, Guid LocalityId)> SeedConfirmedBookingAsync()
    {
        using var context = _db.CreateContext();
        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5));
        string pincodeCode = Guid.NewGuid().ToString("N")[..6];

        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Meera Iyer", CustomerStatus.Active);
        var address = new CustomerAddress(
            Guid.NewGuid(), customer.Id, "Home", "12 MG Road", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Meera Iyer", "9876543210", true);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 999m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var rule = new SlotWindowRule(Guid.NewGuid(), window.Id, futureDate.DayOfWeek);
        var secondWindow = new SlotWindow(Guid.NewGuid(), city.Id, "Afternoon", TimeSpan.FromHours(14), TimeSpan.FromHours(18));
        var secondRule = new SlotWindowRule(Guid.NewGuid(), secondWindow.Id, futureDate.DayOfWeek);

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
        context.SlotWindows.Add(secondWindow);
        context.SlotWindowRules.Add(secondRule);
        context.SaveChanges();

        var request = new BookingSummaryRequest(service.Id, city.Id, address.Id, locality.Id, window.Id, futureDate, Quantity: 1, []);
        var created = await BuildBookingService(context).CreateAsync(customer.Id, request);
        created.IsSuccess.Should().BeTrue();

        var bookingRepository = new BookingRepository(context);
        var booking = await bookingRepository.GetByIdAsync(created.Value.Id);
        booking!.TransitionTo(BookingStatus.Confirmed);
        await bookingRepository.UpdateAsync(booking);

        return (customer.Id, booking.Id, city.Id, locality.Id);
    }

    [Fact]
    public async Task RescheduleAsync_moves_a_confirmed_booking_to_a_different_slot_through_BookingManagementService()
    {
        var (_, bookingId, cityId, localityId) = await SeedConfirmedBookingAsync();

        Guid newWindowId;
        DateOnly newDate;
        using (var context = _db.CreateContext())
        {
            var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
            newDate = booking!.SlotDate.AddDays(2);

            var window = new SlotWindow(Guid.NewGuid(), cityId, "Evening", TimeSpan.FromHours(18), TimeSpan.FromHours(21));
            context.SlotWindows.Add(window);
            context.SlotWindowRules.Add(new SlotWindowRule(Guid.NewGuid(), window.Id, newDate.DayOfWeek));
            newWindowId = window.Id;
            context.SaveChanges();
        }

        using var actContext = _db.CreateContext();
        var service = BuildBookingManagementService(actContext);
        var result = await service.RescheduleAsync(
            bookingId, Guid.NewGuid(), new AdminRescheduleBookingRequest(localityId, newWindowId, newDate, "Customer requested a later slot"));

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Code : "reschedule should succeed for a Confirmed booking");
        // Booking.Reschedule (see its own doc comment) passes through
        // Rescheduled only as an intermediate transition, landing on
        // AwaitingFulfilment - the same final resting state a fresh
        // Confirmed booking would reach once its slot is set.
        result.Value.Status.Should().Be(BookingStatus.AwaitingFulfilment);
        result.Value.Slot.SlotWindowId.Should().Be(newWindowId);
        result.Value.Slot.Date.Should().Be(newDate);
    }

    [Fact]
    public async Task CancelAsync_cancels_a_confirmed_booking_through_BookingManagementService()
    {
        var (_, bookingId, _, _) = await SeedConfirmedBookingAsync();

        using var context = _db.CreateContext();
        var service = BuildBookingManagementService(context);
        var result = await service.CancelAsync(bookingId, Guid.NewGuid(), new AdminCancelBookingRequest("Customer no longer needs the service", "Handled per policy"));

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Code : "cancel should succeed for a Confirmed booking");
        result.Value.Status.Should().Be(BookingStatus.CancelledByAdmin);
    }

    [Fact]
    public async Task UpdateStatusAsync_rejects_Completed_without_a_completion_proof_on_file()
    {
        var (_, bookingId, _, _) = await SeedConfirmedBookingAsync();

        using (var context = _db.CreateContext())
        {
            var bookingRepository = new BookingRepository(context);
            var booking = await bookingRepository.GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.AwaitingFulfilment);
            booking.TransitionTo(BookingStatus.Assigned);
            booking.TransitionTo(BookingStatus.InProgress);
            await bookingRepository.UpdateAsync(booking);
        }

        using var context2 = _db.CreateContext();
        var service = BuildBookingManagementService(context2);
        var result = await service.UpdateStatusAsync(
            bookingId, Guid.NewGuid(), new AdminBookingStatusUpdateRequest(BookingStatus.Completed, "Marking complete"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Booking.CompletionProofRequired");
    }

    [Fact]
    public async Task UpdateStatusAsync_accepts_Completed_once_a_completion_proof_exists()
    {
        var (_, bookingId, _, _) = await SeedConfirmedBookingAsync();

        using (var context = _db.CreateContext())
        {
            var bookingRepository = new BookingRepository(context);
            var booking = await bookingRepository.GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.AwaitingFulfilment);
            booking.TransitionTo(BookingStatus.Assigned);
            booking.TransitionTo(BookingStatus.InProgress);
            await bookingRepository.UpdateAsync(booking);

            var proof = new BookingCompletionProof(Guid.NewGuid(), bookingId, Guid.NewGuid(), ["s3://proofs/photo.jpg"], []);
            await new BookingCompletionProofRepository(context).AddAsync(proof);
        }

        using var context2 = _db.CreateContext();
        var service = BuildBookingManagementService(context2);
        var result = await service.UpdateStatusAsync(
            bookingId, Guid.NewGuid(), new AdminBookingStatusUpdateRequest(BookingStatus.Completed, "Marking complete"));

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Code : "Completed should succeed once a proof is on file");
        result.Value.Status.Should().Be(BookingStatus.Completed);
    }

    /// <summary>
    /// Seeds a real, already-successful <see cref="PaymentTransaction"/> for
    /// the booking directly (skipping the gateway order/webhook round trip
    /// <see cref="FinancialQaSuiteTests"/> exercises elsewhere - not what
    /// this test is about) so <see cref="RefundService"/>'s "no successful
    /// payment to refund" guard has something real to find.
    /// </summary>
    private async Task SeedSuccessfulPaymentAsync(Guid bookingId, Guid customerId, decimal amount)
    {
        using var context = _db.CreateContext();
        var payment = new PaymentTransaction(Guid.NewGuid(), bookingId, customerId, amount, "INR", $"idem-{Guid.NewGuid():N}");
        var attemptId = Guid.NewGuid();
        payment.StartAttempt(attemptId, $"order-{Guid.NewGuid():N}");
        payment.MarkAttemptSucceeded(attemptId, $"pay-{Guid.NewGuid():N}");
        await new PaymentTransactionRepository(context).AddAsync(payment);
    }

    [Fact]
    public async Task RefundAsync_issues_a_full_wallet_refund_through_BookingManagementService_for_an_already_cancelled_booking()
    {
        var (customerId, bookingId, _, _) = await SeedConfirmedBookingAsync();
        await SeedSuccessfulPaymentAsync(bookingId, customerId, 999m);

        // Move the booking to CancelledByAdmin directly (a legal transition
        // per BookingLifecycle) rather than through CancellationService,
        // which would auto-initiate its own refund and leave nothing left
        // for BookingManagementService.RefundAsync itself to exercise.
        using (var context = _db.CreateContext())
        {
            var bookingRepository = new BookingRepository(context);
            var booking = await bookingRepository.GetByIdAsync(bookingId);
            booking!.TransitionTo(BookingStatus.CancelledByAdmin, "Seeded directly for the refund workflow test");
            await bookingRepository.UpdateAsync(booking);
        }

        using var refundContext = _db.CreateContext();
        var refundResult = await BuildBookingManagementService(refundContext)
            .RefundAsync(bookingId, Guid.NewGuid(), new AdminRefundRequest(true, null, "Goodwill full refund", RefundMethod.Wallet));

        refundResult.IsSuccess.Should().BeTrue(because: refundResult.IsFailure ? refundResult.Error.Code : "a full refund should be issuable once a booking has been cancelled");
        refundResult.Value.Status.Should().Be(BookingStatus.Refunded);
        refundResult.Value.Refunds.Should().ContainSingle(r => r.Amount == 999m);
    }

    // ---------------------------------------------------------------
    // 3. Support ticket assign -> respond -> escalate -> resolve.
    // ---------------------------------------------------------------

    private static AdminSupportTicketService BuildSupportTicketService(NestlyDbContext context) =>
        new(
            new SupportTicketRepository(context),
            new AdminUserRepository(context),
            new BookingRepository(context),
            new AuditLogWriter(context, new StubAuditContextProvider(AuditActorType.AdminUser, Guid.NewGuid())));

    [Fact]
    public async Task A_support_ticket_walks_assign_then_respond_then_escalate_then_resolve_through_AdminSupportTicketService()
    {
        Guid customerId, ticketId, adminId;
        using (var context = _db.CreateContext())
        {
            var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Rahul Verma", CustomerStatus.Active);
            context.Add(customer);
            customerId = customer.Id;

            var admin = new AdminUser(Guid.NewGuid(), $"agent-{Guid.NewGuid():N}@nestly.test", "hashed", "Priya Nair");
            context.Add(admin);
            adminId = admin.Id;
            context.SaveChanges();

            var ticket = new SupportTicket(Guid.NewGuid(), customerId, null, SupportTicketCategory.GeneralInquiry, "Question about billing", "I was charged twice.");
            await new SupportTicketRepository(context).AddAsync(ticket);
            ticketId = ticket.Id;
        }

        using (var context = _db.CreateContext())
        {
            var assignResult = await BuildSupportTicketService(context).AssignAsync(ticketId, new AssignSupportTicketRequest(adminId));
            assignResult.IsSuccess.Should().BeTrue();
            assignResult.Value.AssignedAdminUserId.Should().Be(adminId);
        }

        using (var context = _db.CreateContext())
        {
            var respondResult = await BuildSupportTicketService(context).RespondAsync(ticketId, new AddSupportTicketCommentRequest("We're looking into the duplicate charge."));
            respondResult.IsSuccess.Should().BeTrue();
            respondResult.Value.Status.Should().Be(SupportTicketStatus.InProgress);
        }

        using (var context = _db.CreateContext())
        {
            var escalateResult = await BuildSupportTicketService(context).EscalateAsync(ticketId);
            escalateResult.IsSuccess.Should().BeTrue();
            escalateResult.Value.Status.Should().Be(SupportTicketStatus.Escalated);
        }

        using (var context = _db.CreateContext())
        {
            var resolveResult = await BuildSupportTicketService(context).ResolveAsync(ticketId, new ResolveSupportTicketRequest("Duplicate charge refunded."));
            resolveResult.IsSuccess.Should().BeTrue();
            resolveResult.Value.Status.Should().Be(SupportTicketStatus.Resolved);
            resolveResult.Value.ResolutionSummary.Should().Be("Duplicate charge refunded.");
        }
    }

    // ---------------------------------------------------------------
    // 4. A CRUD cycle through the catalog and coupon management services.
    // ---------------------------------------------------------------

    private static ServiceManagementService BuildServiceManagementService(NestlyDbContext context) => new(
        new ServiceRepository(context),
        new CategoryRepository(context),
        new ServiceMediaRepository(context),
        new AuditLogWriter(context, new StubAuditContextProvider(AuditActorType.AdminUser, Guid.NewGuid())));

    [Fact]
    public async Task A_service_is_created_updated_and_deactivated_through_ServiceManagementService()
    {
        Guid categoryId;
        using (var context = _db.CreateContext())
        {
            var category = new Category(Guid.NewGuid(), "Repairs", "repairs-" + Guid.NewGuid(), "desc");
            context.Add(category);
            context.SaveChanges();
            categoryId = category.Id;
        }

        Guid serviceId;
        using (var context = _db.CreateContext())
        {
            var createResult = await BuildServiceManagementService(context).CreateAsync(new ServiceCreateRequest(
                categoryId, "AC Repair", "ac-repair-" + Guid.NewGuid(), "Fixes your AC", null, 1499m,
                "1,2,3", "1,2", null, null, 60, 0, null, null, "Fixed", true, true, true, false, true, true, true));
            createResult.IsSuccess.Should().BeTrue();
            serviceId = createResult.Value.Id;
        }

        using (var context = _db.CreateContext())
        {
            var updateResult = await BuildServiceManagementService(context).UpdateAsync(serviceId, new ServiceUpdateRequest(
                categoryId, "AC Repair (Express)", "ac-repair-express-" + Guid.NewGuid(), "Fixes your AC fast", null, 1799m,
                "1,2,3", "1,2", null, null, 45, 0, null, null, "Fixed", true, true, true, false, true, true, true));
            updateResult.IsSuccess.Should().BeTrue();
            updateResult.Value.Name.Should().Be("AC Repair (Express)");
            updateResult.Value.Price.Should().Be(1799m);
        }

        using (var context = _db.CreateContext())
        {
            var deactivateResult = await BuildServiceManagementService(context).SetActiveAsync(serviceId, isActive: false);
            deactivateResult.IsSuccess.Should().BeTrue();
        }

        using (var context = _db.CreateContext())
        {
            var service = await new ServiceRepository(context).GetByIdAsync(serviceId);
            service!.IsActive.Should().BeFalse();
        }
    }

    private static CouponManagementService BuildCouponManagementService(NestlyDbContext context) => new(
        new CouponRepository(context),
        new CategoryRepository(context),
        context,
        new AuditLogWriter(context, new StubAuditContextProvider(AuditActorType.AdminUser, Guid.NewGuid())));

    [Fact]
    public async Task A_coupon_is_created_updated_and_deactivated_through_CouponManagementService()
    {
        Guid couponId;
        using (var context = _db.CreateContext())
        {
            var createResult = await BuildCouponManagementService(context).CreateAsync(new CouponCreateRequest(
                "SAVE" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                "10% off",
                CouponDiscountType.Percentage,
                10,
                200,
                500,
                DateTime.UtcNow.AddDays(-1),
                DateTime.UtcNow.AddDays(30),
                100,
                1,
                null,
                CouponCustomerSegment.All));
            createResult.IsSuccess.Should().BeTrue();
            couponId = createResult.Value.Id;
        }

        using (var context = _db.CreateContext())
        {
            var updateResult = await BuildCouponManagementService(context).UpdateAsync(couponId, new CouponUpdateRequest(
                "15% off, updated",
                CouponDiscountType.Percentage,
                15,
                250,
                500,
                DateTime.UtcNow.AddDays(-1),
                DateTime.UtcNow.AddDays(45),
                100,
                1,
                null,
                CouponCustomerSegment.All));
            updateResult.IsSuccess.Should().BeTrue();
            updateResult.Value.DiscountValue.Should().Be(15);
        }

        using (var context = _db.CreateContext())
        {
            var deactivateResult = await BuildCouponManagementService(context).DeactivateAsync(couponId);
            deactivateResult.IsSuccess.Should().BeTrue();
        }

        using (var context = _db.CreateContext())
        {
            var coupon = await new CouponRepository(context).GetByIdAsync(couponId);
            coupon!.IsActive.Should().BeFalse();
        }
    }
}
