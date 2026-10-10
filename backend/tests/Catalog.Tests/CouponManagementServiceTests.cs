using Nestly.Application.Settings;
using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Coupons;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Auditing;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>Admin coupon CRUD and redemption reporting (SRS 12.12, task 118).</summary>
public sealed class CouponManagementServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public CouponManagementServiceTests(TestDatabase db) => _db = db;

    private static CouponManagementService BuildService(NestlyDbContext context, IPlatformRules? rules = null) =>
        new(
            new CouponRepository(context),
            new CategoryRepository(context),
            context,
            new AuditLogWriter(context, new StubAuditContextProvider()),
            rules);

    private sealed class StubAuditContextProvider : IAuditContextProvider
    {
        public AuditContext GetCurrent() =>
            new(AuditActorType.AdminUser, Guid.NewGuid(), IpAddress: "127.0.0.1", CorrelationId: "test-correlation-id");
    }

    private static CouponCreateRequest ValidCreateRequest(string? code = null, Guid? categoryId = null) =>
        new(
            Code: code ?? "SAVE" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Description: "10% off",
            DiscountType: CouponDiscountType.Percentage,
            DiscountValue: 10,
            MaxDiscountAmount: 200,
            MinOrderAmount: 500,
            ValidFromUtc: DateTime.UtcNow.AddDays(-1),
            ValidToUtc: DateTime.UtcNow.AddDays(30),
            UsageLimitTotal: 100,
            UsageLimitPerCustomer: 1,
            ApplicableCategoryId: categoryId,
            CustomerSegment: CouponCustomerSegment.All);

    [Fact]
    public async Task CreateAsync_persists_a_coupon_with_every_rule_dimension()
    {
        using var context = _db.CreateContext();
        var service = BuildService(context);
        var request = ValidCreateRequest();

        var result = await service.CreateAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be(request.Code.ToUpperInvariant());
        result.Value.DiscountType.Should().Be(CouponDiscountType.Percentage);
        result.Value.DiscountValue.Should().Be(10);
        result.Value.MaxDiscountAmount.Should().Be(200);
        result.Value.MinOrderAmount.Should().Be(500);
        result.Value.UsageLimitTotal.Should().Be(100);
        result.Value.UsageLimitPerCustomer.Should().Be(1);
        result.Value.IsActive.Should().BeTrue();
        result.Value.RedemptionCount.Should().Be(0);

        using var verifyContext = _db.CreateContext();
        (await new CouponRepository(verifyContext).GetByIdAsync(result.Value.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_rejects_a_duplicate_code()
    {
        using var context = _db.CreateContext();
        var service = BuildService(context);
        string code = "DUPLICATE" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        (await service.CreateAsync(ValidCreateRequest(code))).IsSuccess.Should().BeTrue();
        var second = await service.CreateAsync(ValidCreateRequest(code));

        second.IsFailure.Should().BeTrue();
        second.Error.Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public async Task CreateAsync_rejects_an_unknown_applicable_category()
    {
        using var context = _db.CreateContext();
        var service = BuildService(context);

        var result = await service.CreateAsync(ValidCreateRequest(categoryId: Guid.NewGuid()));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task CreateAsync_resolves_the_applicable_category_name()
    {
        using var context = _db.CreateContext();
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        context.Add(category);
        await context.SaveChangesAsync();

        var service = BuildService(context);
        var result = await service.CreateAsync(ValidCreateRequest(categoryId: category.Id));

        result.IsSuccess.Should().BeTrue();
        result.Value.ApplicableCategoryId.Should().Be(category.Id);
        result.Value.ApplicableCategoryName.Should().Be("Cleaning");
    }

    [Fact]
    public async Task UpdateAsync_applies_every_mutable_rule_dimension_but_never_the_code()
    {
        using var context = _db.CreateContext();
        var service = BuildService(context);
        var created = (await service.CreateAsync(ValidCreateRequest())).Value;

        var update = new CouponUpdateRequest(
            Description: "Updated description",
            DiscountType: CouponDiscountType.Flat,
            DiscountValue: 150,
            MaxDiscountAmount: null,
            MinOrderAmount: 1000,
            ValidFromUtc: created.ValidFromUtc,
            ValidToUtc: created.ValidToUtc.AddDays(10),
            UsageLimitTotal: 50,
            UsageLimitPerCustomer: 2,
            ApplicableCategoryId: null,
            CustomerSegment: CouponCustomerSegment.FirstBookingOnly);

        var result = await service.UpdateAsync(created.Id, update);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be(created.Code);
        result.Value.Description.Should().Be("Updated description");
        result.Value.DiscountType.Should().Be(CouponDiscountType.Flat);
        result.Value.DiscountValue.Should().Be(150);
        result.Value.MaxDiscountAmount.Should().BeNull();
        result.Value.MinOrderAmount.Should().Be(1000);
        result.Value.UsageLimitTotal.Should().Be(50);
        result.Value.UsageLimitPerCustomer.Should().Be(2);
        result.Value.CustomerSegment.Should().Be(CouponCustomerSegment.FirstBookingOnly);
    }

    [Fact]
    public async Task UpdateAsync_returns_not_found_for_an_unknown_coupon()
    {
        using var context = _db.CreateContext();
        var service = BuildService(context);

        var result = await service.UpdateAsync(Guid.NewGuid(), new CouponUpdateRequest(
            null, CouponDiscountType.Flat, 100, null, 0, DateTime.UtcNow, DateTime.UtcNow.AddDays(1), null, null, null, CouponCustomerSegment.All));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task ActivateAsync_and_DeactivateAsync_toggle_the_active_flag()
    {
        using var context = _db.CreateContext();
        var service = BuildService(context);
        var created = (await service.CreateAsync(ValidCreateRequest())).Value;
        created.IsActive.Should().BeTrue();

        (await service.DeactivateAsync(created.Id)).IsSuccess.Should().BeTrue();
        (await service.GetByIdAsync(created.Id)).Value.IsActive.Should().BeFalse();

        (await service.ActivateAsync(created.Id)).IsSuccess.Should().BeTrue();
        (await service.GetByIdAsync(created.Id)).Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SearchAsync_filters_by_code_and_active_status_with_pagination()
    {
        using var context = _db.CreateContext();
        var service = BuildService(context);

        string sharedPrefix = "SEARCH" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var first = (await service.CreateAsync(ValidCreateRequest($"{sharedPrefix}-A"))).Value;
        var second = (await service.CreateAsync(ValidCreateRequest($"{sharedPrefix}-B"))).Value;
        await service.DeactivateAsync(second.Id);

        var activeOnly = await service.SearchAsync(new CouponAdminSearchRequest(
            Code: sharedPrefix, IsActive: true, DiscountType: null, CustomerSegment: null,
            ApplicableCategoryId: null, ValidOnUtc: null, Page: 1, PageSize: 20));

        activeOnly.TotalCount.Should().Be(1);
        activeOnly.Items.Single().Id.Should().Be(first.Id);

        var both = await service.SearchAsync(new CouponAdminSearchRequest(
            Code: sharedPrefix, IsActive: null, DiscountType: null, CustomerSegment: null,
            ApplicableCategoryId: null, ValidOnUtc: null, Page: 1, PageSize: 20));

        both.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task GetRedemptionReportAsync_aggregates_redemptions_discount_total_and_unique_customers()
    {
        using var context = _db.CreateContext();
        var service = BuildService(context);
        var coupon = (await service.CreateAsync(ValidCreateRequest())).Value;

        var customerA = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var customerB = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Rahul Iyer", CustomerStatus.Active);
        context.Add(customerA);
        context.Add(customerB);
        var bookingOne = CreateBooking(customerA.Id);
        var bookingTwo = CreateBooking(customerA.Id);
        var bookingThree = CreateBooking(customerB.Id);
        context.Add(bookingOne);
        context.Add(bookingTwo);
        context.Add(bookingThree);
        await context.SaveChangesAsync();

        // Two redemptions by customerA (allowed since UsageLimitPerCustomer
        // was raised via UpdateAsync in a real flow; here the report is
        // exercised directly against CouponRedemption rows, independent of
        // whether ICouponService would have allowed this sequence) plus one
        // by customerB - three redemptions, two unique customers.
        context.CouponRedemptions.Add(new CouponRedemption(Guid.NewGuid(), coupon.Id, customerA.Id, bookingOne.Id, 50m));
        context.CouponRedemptions.Add(new CouponRedemption(Guid.NewGuid(), coupon.Id, customerA.Id, bookingTwo.Id, 60m));
        context.CouponRedemptions.Add(new CouponRedemption(Guid.NewGuid(), coupon.Id, customerB.Id, bookingThree.Id, 40m));
        await context.SaveChangesAsync();

        // Scoped to this coupon specifically - the TestDatabase fixture's
        // in-memory database is shared across every test in this class (see
        // TestDatabase's doc comment), so an unscoped, all-coupons query
        // would also pick up redemptions inserted by sibling tests.
        var report = await service.GetRedemptionReportAsync(new CouponRedemptionReportRequest(coupon.Id, null, null));

        report.IsSuccess.Should().BeTrue();
        report.Value.TotalRedemptions.Should().Be(3);
        report.Value.TotalDiscountAmount.Should().Be(150m);
        var row = report.Value.Rows.Single(r => r.CouponId == coupon.Id);
        row.RedemptionCount.Should().Be(3);
        row.TotalDiscountAmount.Should().Be(150m);
        row.UniqueCustomerCount.Should().Be(2);
    }

    [Fact]
    public async Task GetRedemptionReportAsync_scopes_to_a_single_coupon_and_date_range()
    {
        using var context = _db.CreateContext();
        var service = BuildService(context);
        var couponOne = (await service.CreateAsync(ValidCreateRequest())).Value;
        var couponTwo = (await service.CreateAsync(ValidCreateRequest())).Value;

        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        context.Add(customer);
        var bookingOne = CreateBooking(customer.Id);
        var bookingTwo = CreateBooking(customer.Id);
        context.Add(bookingOne);
        context.Add(bookingTwo);
        await context.SaveChangesAsync();

        context.CouponRedemptions.Add(new CouponRedemption(Guid.NewGuid(), couponOne.Id, customer.Id, bookingOne.Id, 30m));
        context.CouponRedemptions.Add(new CouponRedemption(Guid.NewGuid(), couponTwo.Id, customer.Id, bookingTwo.Id, 70m));
        await context.SaveChangesAsync();

        var scoped = await service.GetRedemptionReportAsync(new CouponRedemptionReportRequest(couponOne.Id, null, null));

        scoped.IsSuccess.Should().BeTrue();
        scoped.Value.Rows.Should().ContainSingle();
        scoped.Value.Rows.Single().CouponId.Should().Be(couponOne.Id);
        scoped.Value.TotalDiscountAmount.Should().Be(30m);

        var futureOnly = await service.GetRedemptionReportAsync(
            new CouponRedemptionReportRequest(null, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(2)));

        futureOnly.IsSuccess.Should().BeTrue();
        futureOnly.Value.Rows.Should().BeEmpty();
        futureOnly.Value.TotalRedemptions.Should().Be(0);
    }

    [Fact]
    public async Task GetRedemptionReportAsync_rejects_an_inverted_date_range()
    {
        using var context = _db.CreateContext();
        var service = BuildService(context);

        var result = await service.GetRedemptionReportAsync(
            new CouponRedemptionReportRequest(null, DateTime.UtcNow, DateTime.UtcNow.AddDays(-1)));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
    }

    /// <summary>
    /// A self-contained, snapshot-based Booking (no Service/Category/Slot
    /// seeding needed) purely to satisfy CouponRedemption's foreign key -
    /// same helper CustomerManagementServiceTests uses for the same reason.
    /// </summary>
    private static Booking CreateBooking(Guid customerId) =>
        new(
            Guid.NewGuid(),
            customerId,
            new CustomerSnapshot("Test Customer", "9000000000"),
            null,
            new AddressSnapshot("Home", "12 Main St", null, null, "560001", "Bengaluru", "Karnataka", 12.9m, 77.5m, "Test Customer", "9000000000"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(11)),
            new PriceSnapshot(500m, 1, 500m, 0m, 0m, 500m, 18m, 90m, 10m, 600m));

    // ---------------------------------------------------------------------------------------------------------------
    // Coupon rules (Settings -> Coupons): the highest percentage a coupon may give.
    // ---------------------------------------------------------------------------------------------------------------

    private static IPlatformRules MaxPercent(decimal percentage) =>
        TestServices.Rules(coupon: new CouponSettings(percentage, null, AllowCouponStacking: false, CouponsEnabled: true));

    [Fact]
    public async Task CreateAsync_refuses_a_percentage_above_the_saved_maximum()
    {
        using var context = _db.CreateContext();
        var result = await BuildService(context, MaxPercent(20)).CreateAsync(ValidCreateRequest() with { DiscountValue = 30 });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Coupon.DiscountAboveLimit");
        result.Error.Message.Should().Contain("20");
    }

    [Fact]
    public async Task CreateAsync_allows_a_percentage_at_the_saved_maximum_and_any_flat_amount()
    {
        using var context = _db.CreateContext();
        var service = BuildService(context, MaxPercent(20));

        (await service.CreateAsync(ValidCreateRequest() with { DiscountValue = 20 })).IsSuccess.Should().BeTrue();
        (await service.CreateAsync(ValidCreateRequest() with { DiscountType = CouponDiscountType.Flat, DiscountValue = 900 })).IsSuccess.Should().BeTrue(
            "the maximum is a percentage; a flat amount is not one");
    }

    [Fact]
    public async Task CreateAsync_with_no_coupon_rules_saved_allows_what_it_always_did()
    {
        using var context = _db.CreateContext();
        (await BuildService(context, TestServices.Rules()).CreateAsync(ValidCreateRequest() with { DiscountValue = 90 })).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_refuses_raising_a_percentage_above_the_maximum_but_lets_a_legacy_coupon_be_edited_otherwise()
    {
        Guid legacyId, modestId;
        using (var context = _db.CreateContext())
        {
            // Created before any maximum was saved.
            legacyId = (await BuildService(context).CreateAsync(ValidCreateRequest() with { DiscountValue = 60 })).Value.Id;
            modestId = (await BuildService(context).CreateAsync(ValidCreateRequest() with { DiscountValue = 10 })).Value.Id;
        }

        CouponUpdateRequest Update(decimal value, string description) => new(
            description, CouponDiscountType.Percentage, value, 200, 500, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30), 100, 1, null, CouponCustomerSegment.All);

        using var editContext = _db.CreateContext();
        var service = BuildService(editContext, MaxPercent(50));

        (await service.UpdateAsync(legacyId, Update(60, "Legacy, new wording"))).IsSuccess.Should().BeTrue(
            "its percentage is not being raised, so a coupon that already exceeds the maximum can still have its text changed");
        (await service.UpdateAsync(legacyId, Update(70, "Legacy, raised"))).Error.Code.Should().Be("Coupon.DiscountAboveLimit");
        (await service.UpdateAsync(modestId, Update(55, "Modest, pushed past"))).Error.Code.Should().Be("Coupon.DiscountAboveLimit");
        (await service.UpdateAsync(modestId, Update(40, "Modest, within"))).IsSuccess.Should().BeTrue();
    }
}
