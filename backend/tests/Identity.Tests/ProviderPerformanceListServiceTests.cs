using FluentAssertions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// <c>ProviderManagementService.ListPerformanceAsync</c> - the ranking list
/// docs/OPEN-FIXES-FEATURES.csv "Provider performance" asks for. Covers what
/// <see cref="ProviderPerformanceServiceTests"/> does not: the rolling
/// window filter and sorting/paging over more than one provider.
/// </summary>
public class ProviderPerformanceListServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Guid _adminUserId = Guid.NewGuid();

    private static ProviderManagementService CreateService(NestlyDbContext context) => new(
        new ProviderRepository(context),
        new ProviderKycDocumentRepository(context),
        new ProviderBackgroundCheckRepository(context),
        new BookingRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderEarningLedgerRepository(context),
        new ProviderCapacityRepository(context),
        new ProviderServiceAreaRepository(context),
        new ProviderSessionRepository(context),
        new ServiceabilityMappingManagementService(
            new CategoryCityMappingRepository(context), new ServicePincodeMappingRepository(context), new CategoryRepository(context),
            new CityRepository(context), new ServiceRepository(context), new PincodeRepository(context)),
        new ProviderAvailabilityWindowRepository(context),
        new ReviewRepository(context));

    private static BookingProviderAssignmentService CreateAssignmentService(NestlyDbContext context) => new(
        new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context), new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions()), context);

    private static Provider NewActiveProvider(string legalName, string displayName, string phone)
    {
        var provider = new Provider(Guid.NewGuid(), legalName, displayName, ProviderType.Individual, phone);
        provider.ChangeStatus(ProviderStatus.Active);
        return provider;
    }

    /// <summary>See <see cref="ProviderPerformanceServiceTests.NewAwaitingFulfilmentBooking"/>'s doc comment for why <paramref name="startHour"/> exists.</summary>
    private static Booking NewAwaitingFulfilmentBooking(Guid customerId, int startHour = 9)
    {
        var booking = new Booking(
            Guid.NewGuid(), customerId,
            new CustomerSnapshot("Asha Rao", "9876543210"),
            null,
            new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(startHour), TimeSpan.FromHours(startHour + 3)),
            new PriceSnapshot(999m, 1, 999m, 0m, 0m, 999m, 0m, 0m, 0m, 999m));
        booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Deep Cleaning", "deep-cleaning", 999m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        return booking;
    }

    private static async Task<Guid> SeedBookingAsync(NestlyDbContext context, int startHour = 9)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        await context.AddAsync(customer);

        var booking = NewAwaitingFulfilmentBooking(customer.Id, startHour);
        await context.AddAsync(booking);
        await context.SaveChangesAsync();
        return booking.Id;
    }

    /// <summary>A real Customer/Service row for a <see cref="Review"/>'s FK-constrained columns (ReviewConfiguration) - the booking id it is attached to just needs to be some real Booking row, not necessarily the one the assignment flow used.</summary>
    private static async Task<Guid> SeedReviewAsync(NestlyDbContext context, Guid bookingId, Guid providerId, int rating)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Priya Nair", CustomerStatus.Active);
        var category = new Category(Guid.NewGuid(), "Cleaning " + Guid.NewGuid().ToString("N")[..6], "cleaning-" + Guid.NewGuid().ToString("N")[..6], "Home cleaning services");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid().ToString("N")[..6], "desc", 500m);
        await context.AddRangeAsync(customer, category, service);
        await context.SaveChangesAsync();

        var review = new Review(Guid.NewGuid(), bookingId, customer.Id, service.Id, providerId, rating, reviewText: null);
        await new ReviewRepository(context).AddAsync(review);
        return review.Id;
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task ListPerformanceAsync_excludes_offers_made_before_the_requested_window()
    {
        await using var context = _database.CreateContext();
        var provider = NewActiveProvider("Ravi Kumar", "Ravi's Repairs", "+919876543210");
        await context.AddAsync(provider);
        await context.SaveChangesAsync();

        var assignmentService = CreateAssignmentService(context);

        var recentBookingId = await SeedBookingAsync(context, startHour: 9);
        (await assignmentService.AssignAsync(recentBookingId, _adminUserId, new AssignProviderRequest(provider.Id, ResponseDeadline: null)))
            .IsSuccess.Should().BeTrue();

        var oldBookingId = await SeedBookingAsync(context, startHour: 13);
        (await assignmentService.AssignAsync(oldBookingId, _adminUserId, new AssignProviderRequest(provider.Id, ResponseDeadline: null)))
            .IsSuccess.Should().BeTrue();

        // Backdates the second offer outside the 30-day window this test
        // requests below. EF's Entry API sets the mapped column directly
        // rather than through the domain's own (private-setter) mutators,
        // which expose no way to construct an assignment in the past - the
        // one legitimate reason to reach around the aggregate in a test.
        var oldAssignment = await new BookingProviderAssignmentRepository(context).GetActiveByBookingAsync(oldBookingId);
        context.Entry(oldAssignment!).Property(nameof(BookingProviderAssignment.AssignedAt)).CurrentValue = DateTime.UtcNow.AddDays(-40);
        await context.SaveChangesAsync();

        var result = await CreateService(context).ListPerformanceAsync(
            new ProviderPerformanceListRequest(Page: 1, PageSize: 20, PeriodDays: 30));

        result.IsSuccess.Should().BeTrue();
        var row = result.Value.Items.Should().ContainSingle(r => r.ProviderId == provider.Id).Subject;
        row.OffersReceived.Should().Be(1, "the 40-day-old offer falls outside the 30-day window");
    }

    [Fact]
    public async Task ListPerformanceAsync_sorts_by_average_rating_with_no_rating_always_last()
    {
        await using var context = _database.CreateContext();
        var rated = NewActiveProvider("Ravi Kumar", "Ravi's Repairs", "+919876543210");
        var unrated = NewActiveProvider("Meena Iyer", "Meena's Services", "+919876500000");
        await context.AddRangeAsync(rated, unrated);
        await context.SaveChangesAsync();

        var bookingId = await SeedBookingAsync(context);
        await SeedReviewAsync(context, bookingId, rated.Id, rating: 5);

        var service = CreateService(context);

        var descending = await service.ListPerformanceAsync(
            new ProviderPerformanceListRequest(Page: 1, PageSize: 20, SortBy: ProviderPerformanceSortField.AverageRating, SortDescending: true));
        descending.IsSuccess.Should().BeTrue();
        descending.Value.Items.Select(r => r.ProviderId).Should().Equal(
            new[] { rated.Id, unrated.Id },
            "descending by rating still puts 'no rating yet' after every real number, not before");

        var ascending = await service.ListPerformanceAsync(
            new ProviderPerformanceListRequest(Page: 1, PageSize: 20, SortBy: ProviderPerformanceSortField.AverageRating, SortDescending: false));
        ascending.IsSuccess.Should().BeTrue();
        ascending.Value.Items.Select(r => r.ProviderId).Should().Equal(
            new[] { rated.Id, unrated.Id },
            "ascending by rating still puts 'no rating yet' last, not first as a literal-zero sort would");
    }

    [Fact]
    public async Task ListPerformanceAsync_paginates_the_ranked_set()
    {
        await using var context = _database.CreateContext();
        for (int i = 0; i < 3; i++)
        {
            await context.AddAsync(NewActiveProvider($"Provider {i}", $"Provider {i}", $"+9198765{i:D5}"));
        }
        await context.SaveChangesAsync();

        var result = await CreateService(context).ListPerformanceAsync(
            new ProviderPerformanceListRequest(Page: 1, PageSize: 2));

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(3, "every provider counts toward the total even though only a page is returned");
        result.Value.Items.Should().HaveCount(2);
        result.Value.Page.Should().Be(1);
        result.Value.PageSize.Should().Be(2);
    }
}
