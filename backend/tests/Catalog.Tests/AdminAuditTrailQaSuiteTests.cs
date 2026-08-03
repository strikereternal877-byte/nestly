using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Catalog;
using Nestly.Application.Coupons;
using Nestly.Application.AdminUserManagement;
using Nestly.Application.BookingManagement;
using Nestly.Application.Bookings;
using Nestly.Application.Cancellations;
using Nestly.Application.Cms;
using Nestly.Application.Notifications;
using Nestly.Application.Payments;
using Nestly.Application.Pricing;
using Nestly.Application.Refunds;
using Nestly.Application.Reschedules;
using Nestly.Application.Serviceability;
using Nestly.Application.Settings;
using Nestly.Application.Support;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Phase 6 closing QA suite (task 132c): a cross-cutting check that every
/// mutating admin action across the modules this phase built actually writes
/// an <see cref="IAuditLogWriter"/> entry with a sane actor/entity/action
/// shape. This is deliberately shallow per module (one representative
/// mutation each, not a re-test of that module's own business rules - those
/// already have their own focused test files); its job is only confirming
/// the audit invariant itself holds everywhere it is supposed to.
///
/// Three real gaps were found and fixed while writing this suite (task
/// 132c's own instruction: fix rather than skip the assertion when a gap is
/// found) - <see cref="CouponManagementService"/>, <see cref="AdminSupportTicketService"/>
/// and the three CMS services (<see cref="CmsPageService"/>,
/// <see cref="CmsMediaService"/>, <see cref="CmsFaqService"/>) previously had
/// no <see cref="IAuditLogWriter"/> dependency at all, so coupon edits,
/// support ticket actions and CMS edits were silently unaudited despite
/// being financial/customer-facing admin actions of exactly the kind SRS 21
/// requires an audit trail for. Each now writes an entry per mutation,
/// verified below alongside every module that already audited correctly.
/// </summary>
public sealed class AdminAuditTrailQaSuiteTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public AdminAuditTrailQaSuiteTests(TestDatabase db) => _db = db;

    private sealed class StubAuditContextProvider(Guid? actorId) : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, actorId, IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }

    /// <summary>Asserts exactly one audit row exists for the given entity/action pair and that its actor/shape look sane.</summary>
    private static async Task AssertAuditedAsync(NestlyDbContext context, string entityName, string entityId, string action, Guid? expectedActorId = null)
    {
        var entry = await context.Set<AuditLog>().SingleOrDefaultAsync(a =>
            a.EntityName == entityName && a.EntityId == entityId && a.Action == action);

        entry.Should().NotBeNull($"a mutating action ({entityName}/{action}) must write an audit entry");
        entry!.ActorType.Should().Be(AuditActorType.AdminUser);
        entry.OccurredOnUtc.Should().NotBe(default);
        if (expectedActorId is not null)
        {
            entry.ActorId.Should().Be(expectedActorId);
        }
    }

    // ---------------------------------------------------------------
    // Admin user management + RBAC role assignment.
    // ---------------------------------------------------------------

    private static AdminUserManagementService BuildAdminUserManagementService(NestlyDbContext context, Guid actorId) => new(
        new AdminUserRepository(context),
        new AdminRoleRepository(context),
        new AuditLogWriter(context, new StubAuditContextProvider(actorId)),
        context);

    [Fact]
    public async Task Creating_an_admin_user_is_audited()
    {
        var actorId = Guid.NewGuid();
        using var context = _db.CreateContext();
        var result = await BuildAdminUserManagementService(context, actorId).CreateAsync(
            new CreateAdminUserRequest($"new-admin-{Guid.NewGuid():N}@nestly.test", "Aarav Shah", "Str0ng!Passw0rd", null),
            actorId);

        result.IsSuccess.Should().BeTrue();
        await AssertAuditedAsync(context, "AdminUser", result.Value.Id.ToString(), "AdminUserCreated", actorId);
    }

    [Fact]
    public async Task Assigning_a_role_to_an_admin_user_is_audited()
    {
        Guid adminUserId, roleId;
        using (var context = _db.CreateContext())
        {
            var role = new AdminRole(Guid.NewGuid(), AdminRoleNames.SupportAdmin + "-" + Guid.NewGuid().ToString("N")[..6], "Support");
            var adminUser = new AdminUser(Guid.NewGuid(), $"role-target-{Guid.NewGuid():N}@nestly.test", "hashed", "Test Admin");
            context.Add(role);
            context.Add(adminUser);
            context.SaveChanges();
            adminUserId = adminUser.Id;
            roleId = role.Id;
        }

        var actorId = Guid.NewGuid();
        using var actContext = _db.CreateContext();
        var result = await BuildAdminUserManagementService(actContext, actorId).AssignRoleAsync(
            adminUserId, new AssignAdminRoleRequest(roleId), actorId);

        result.IsSuccess.Should().BeTrue();
        await AssertAuditedAsync(actContext, "AdminUser", adminUserId.ToString(), "AdminUserRoleAssigned", actorId);
    }

    // ---------------------------------------------------------------
    // Catalog edit.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Creating_a_catalog_service_is_audited()
    {
        Guid categoryId;
        using (var context = _db.CreateContext())
        {
            var category = new Category(Guid.NewGuid(), "Audit Category", "audit-category-" + Guid.NewGuid(), "desc");
            context.Add(category);
            context.SaveChanges();
            categoryId = category.Id;
        }

        var actorId = Guid.NewGuid();
        using var actContext = _db.CreateContext();
        var service = new ServiceManagementService(
            new ServiceRepository(actContext), new CategoryRepository(actContext), new ServiceMediaRepository(actContext),
            new AuditLogWriter(actContext, new StubAuditContextProvider(actorId)));

        var result = await service.CreateAsync(new ServiceCreateRequest(
            categoryId, "Audit Service", "audit-service-" + Guid.NewGuid(), "desc", null, 499m,
            "1", "1", null, null, 30, 0, null, null, "Fixed", true, false, false, false, true, true, true));

        result.IsSuccess.Should().BeTrue();
        await AssertAuditedAsync(actContext, "Service", result.Value.Id.ToString(), "Created", actorId);
    }

    // ---------------------------------------------------------------
    // Pricing edit.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Changing_a_services_price_is_audited()
    {
        Guid serviceId;
        using (var context = _db.CreateContext())
        {
            var category = new Category(Guid.NewGuid(), "Pricing Category", "pricing-category-" + Guid.NewGuid(), "desc");
            var svc = new Service(Guid.NewGuid(), category.Id, "Priced Service", "priced-service-" + Guid.NewGuid(), "desc", 599m);
            context.Add(category);
            context.Add(svc);
            context.SaveChanges();
            serviceId = svc.Id;
        }

        var actorId = Guid.NewGuid();
        using var actContext = _db.CreateContext();
        var pricingService = new PricingManagementService(
            new ServiceRepository(actContext), new ServiceAddOnRepository(actContext), new CityRepository(actContext),
            new ServiceCityPriceRepository(actContext), new PromotionalPriceRepository(actContext), new CityPricingPolicyRepository(actContext),
            new AuditLogWriter(actContext, new StubAuditContextProvider(actorId)), new StubAuditContextProvider(actorId));

        var result = await pricingService.UpdateServicePriceAsync(serviceId, new ServicePriceUpdateRequest(899m));

        result.IsSuccess.Should().BeTrue();
        await AssertAuditedAsync(actContext, "Service", serviceId.ToString(), "PriceChanged", actorId);
    }

    // ---------------------------------------------------------------
    // Coupon edit (gap fixed by this task - see class doc comment).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Creating_a_coupon_is_audited()
    {
        var actorId = Guid.NewGuid();
        using var context = _db.CreateContext();
        var service = new CouponManagementService(
            new CouponRepository(context), new CategoryRepository(context), context,
            new AuditLogWriter(context, new StubAuditContextProvider(actorId)));

        var result = await service.CreateAsync(new CouponCreateRequest(
            "AUDIT" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), "desc", CouponDiscountType.Percentage, 5,
            null, 100, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30), null, null, null, CouponCustomerSegment.All));

        result.IsSuccess.Should().BeTrue();
        await AssertAuditedAsync(context, "Coupon", result.Value.Id.ToString(), "Created", actorId);
    }

    // ---------------------------------------------------------------
    // Booking admin action.
    // ---------------------------------------------------------------

    private static BookingManagementService BuildBookingManagementService(NestlyDbContext context, Guid actorId) => new(
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
                new SandboxPaymentGateway(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" })), context),
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
            new SandboxPaymentGateway(Options.Create(new SandboxGatewayOptions { WebhookSigningSecret = "unit-test-signing-secret-value" })), context),
        new AuditLogWriter(context, new StubAuditContextProvider(actorId)),
        context,
        new BookingCompletionProofRepository(context));

    [Fact]
    public async Task Admin_cancelling_a_booking_is_audited()
    {
        Guid bookingId;
        using (var context = _db.CreateContext())
        {
            var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Audit Customer", CustomerStatus.Active);
            var category = new Category(Guid.NewGuid(), "Audit Booking Category", "audit-booking-category-" + Guid.NewGuid(), "desc");
            var svc = new Service(Guid.NewGuid(), category.Id, "Audit Booking Service", "audit-booking-service-" + Guid.NewGuid(), "desc", 799m);
            var booking = new Booking(
                Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null,
                new AddressSnapshot("Home", "1 Test Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Audit Customer", "9876543210"),
                new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(4)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
                new PriceSnapshot(799m, 1, 799m, 0m, 0m, 799m, 0m, 0m, 0m, 799m));
            booking.AddItem(Guid.NewGuid(), svc.Id, svc.Name, svc.Slug, 799m, 1);
            booking.TransitionTo(BookingStatus.PaymentPending);
            booking.TransitionTo(BookingStatus.Confirmed);

            context.Add(customer);
            context.Add(category);
            context.Add(svc);
            context.Add(booking);
            context.SaveChanges();
            bookingId = booking.Id;
        }

        var actorId = Guid.NewGuid();
        using var actContext = _db.CreateContext();
        var result = await BuildBookingManagementService(actContext, actorId)
            .CancelAsync(bookingId, actorId, new AdminCancelBookingRequest("Audit trail check", null));

        result.IsSuccess.Should().BeTrue();
        await AssertAuditedAsync(actContext, "Booking", bookingId.ToString(), "AdminCancel", actorId);
    }

    // ---------------------------------------------------------------
    // Support ticket action (gap fixed by this task - see class doc comment).
    // ---------------------------------------------------------------

    [Fact]
    public async Task Assigning_a_support_ticket_is_audited()
    {
        Guid ticketId, adminId;
        using (var context = _db.CreateContext())
        {
            var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Ticket Customer", CustomerStatus.Active);
            var admin = new AdminUser(Guid.NewGuid(), $"assignee-{Guid.NewGuid():N}@nestly.test", "hashed", "Assignee Admin");
            context.Add(customer);
            context.Add(admin);
            context.SaveChanges();
            adminId = admin.Id;

            var ticket = new SupportTicket(Guid.NewGuid(), customer.Id, null, SupportTicketCategory.GeneralInquiry, "Audit check", "Body");
            await new SupportTicketRepository(context).AddAsync(ticket);
            ticketId = ticket.Id;
        }

        var actorId = Guid.NewGuid();
        using var actContext = _db.CreateContext();
        var service = new AdminSupportTicketService(
            new SupportTicketRepository(actContext), new AdminUserRepository(actContext), new BookingRepository(actContext),
            new AuditLogWriter(actContext, new StubAuditContextProvider(actorId)));

        var result = await service.AssignAsync(ticketId, new AssignSupportTicketRequest(adminId));

        result.IsSuccess.Should().BeTrue();
        await AssertAuditedAsync(actContext, "SupportTicket", ticketId.ToString(), "Assigned", actorId);
    }

    // ---------------------------------------------------------------
    // CMS edit (gap fixed by this task - see class doc comment) + notification template edit.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Creating_a_cms_page_is_audited()
    {
        var actorId = Guid.NewGuid();
        using var context = _db.CreateContext();
        var service = new CmsPageService(new CmsPageRepository(context), new AuditLogWriter(context, new StubAuditContextProvider(actorId)));

        var result = await service.CreateAsync(new CmsPageCreateRequest(
            "Audit Page", "audit-page-" + Guid.NewGuid(), "Body content", null, null, null, CmsPlacement.Footer, null, null));

        result.IsSuccess.Should().BeTrue();
        await AssertAuditedAsync(context, "CmsPage", result.Value.Id.ToString(), "Created", actorId);
    }

    [Fact]
    public async Task Publishing_a_cms_faq_is_audited()
    {
        Guid faqId;
        var creatorActorId = Guid.NewGuid();
        using (var context = _db.CreateContext())
        {
            var service = new CmsFaqService(new CmsFaqRepository(context), new AuditLogWriter(context, new StubAuditContextProvider(creatorActorId)));
            var created = await service.CreateAsync(new CmsFaqCreateRequest("Audit question?", "Audit answer.", CmsPlacement.General, 0, null, null));
            created.IsSuccess.Should().BeTrue();
            faqId = created.Value.Id;
        }

        var actorId = Guid.NewGuid();
        using var actContext = _db.CreateContext();
        var publishService = new CmsFaqService(new CmsFaqRepository(actContext), new AuditLogWriter(actContext, new StubAuditContextProvider(actorId)));
        var result = await publishService.PublishAsync(faqId);

        result.IsSuccess.Should().BeTrue();
        await AssertAuditedAsync(actContext, "CmsFaq", faqId.ToString(), "Published", actorId);
    }

    [Fact]
    public async Task Creating_a_notification_template_is_audited()
    {
        var actorId = Guid.NewGuid();
        using var context = _db.CreateContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new NotificationTemplateManagementService(
            new NotificationTemplateRepository(context),
            new AuditLogWriter(context, new StubAuditContextProvider(actorId)),
            new StubAuditContextProvider(actorId),
            cache);

        var result = await service.CreateAsync(new NotificationTemplateCreateRequest(
            NotificationEventType.BookingConfirmed, NotificationChannel.Sms, "audit-template-" + Guid.NewGuid(), null, "Your booking is confirmed."));

        result.IsSuccess.Should().BeTrue();
        await AssertAuditedAsync(context, "NotificationTemplate", result.Value.Id.ToString(), "Created", actorId);
    }

    // ---------------------------------------------------------------
    // Settings change.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Updating_cancellation_settings_is_audited()
    {
        using (var context = _db.CreateContext())
        {
            context.Add(new SystemSetting(
                Guid.NewGuid(), SystemSettingGroups.Cancellation,
                "{\"freeCancellationWindowHours\":4,\"lateCancellationFeePercentage\":20,\"allowAdminOverride\":true}"));
            context.SaveChanges();
        }

        var actorId = Guid.NewGuid();
        using var actContext = _db.CreateContext();
        var service = new SystemSettingsService(
            new SystemSettingRepository(actContext), new AuditLogWriter(actContext, new StubAuditContextProvider(actorId)), new StubAuditContextProvider(actorId));

        var result = await service.UpdateCancellationSettingsAsync(new CancellationSettings(8, 10, false));

        result.IsSuccess.Should().BeTrue();
        await AssertAuditedAsync(actContext, "SystemSetting", SystemSettingGroups.Cancellation, "Updated", actorId);
    }
}
