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
/// Task 263: <c>ProviderManagementService.GetPerformanceAsync</c> had no test
/// coverage at all, which is how it shipped counting the wrong set of
/// assignments.
///
/// It built its assignment set from
/// <c>IBookingRepository.ListByAssignedProviderAsync</c>, which filters on
/// <c>Booking.AssignedProviderId</c> - the live assignment only. A booking a
/// provider rejects is reassigned to somebody else, so it stops pointing at
/// the rejecting provider and that provider's Rejected row disappeared from
/// the count. Rejection is precisely the case that gets reassigned away, so
/// RejectedAssignments reported near-zero no matter how often a provider
/// actually declined work.
/// </summary>
public class ProviderPerformanceServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Guid _providerId;
    private readonly Guid _replacementProviderId;
    private readonly Guid _adminUserId = Guid.NewGuid();

    public ProviderPerformanceServiceTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        var replacement = new Provider(Guid.NewGuid(), "Meena Iyer", "Meena's Services", ProviderType.Individual, "+919876500000");
        provider.ChangeStatus(ProviderStatus.Active);
        replacement.ChangeStatus(ProviderStatus.Active);
        _providerId = provider.Id;
        _replacementProviderId = replacement.Id;
        context.AddRange(provider, replacement);
        context.SaveChanges();
    }

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
        new Nestly.Infrastructure.Services.ServiceabilityMappingManagementService(
            new CategoryCityMappingRepository(context), new ServicePincodeMappingRepository(context), new CategoryRepository(context),
            new CityRepository(context), new ServiceRepository(context), new PincodeRepository(context)));

    private static BookingProviderAssignmentService CreateAssignmentService(NestlyDbContext context) => new(
        new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context), new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions()), context);

    private static Booking NewAwaitingFulfilmentBooking(Guid customerId)
    {
        var booking = new Booking(
            Guid.NewGuid(), customerId,
            new CustomerSnapshot("Asha Rao", "9876543210"),
            null,
            new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0m, 0m, 999m, 0m, 0m, 0m, 999m));
        booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Deep Cleaning", "deep-cleaning", 999m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        return booking;
    }

    private async Task<Guid> SeedBookingAsync(NestlyDbContext context)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        await context.AddAsync(customer);

        var booking = NewAwaitingFulfilmentBooking(customer.Id);
        await context.AddAsync(booking);
        await context.SaveChangesAsync();
        return booking.Id;
    }

    [Fact]
    public async Task GetPerformanceAsync_counts_a_rejection_even_after_the_booking_is_reassigned_away()
    {
        await using var context = _database.CreateContext();
        var assignmentService = CreateAssignmentService(context);
        var bookingId = await SeedBookingAsync(context);

        (await assignmentService.AssignAsync(bookingId, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null)))
            .IsSuccess.Should().BeTrue();
        (await assignmentService.RejectAsync(bookingId, new RejectAssignmentRequest("Not available")))
            .IsSuccess.Should().BeTrue();

        // The booking moves on to somebody else, so its AssignedProviderId no
        // longer points at the provider who rejected it.
        (await assignmentService.AssignAsync(bookingId, _adminUserId, new AssignProviderRequest(_replacementProviderId, ResponseDeadline: null)))
            .IsSuccess.Should().BeTrue();

        var result = await CreateService(context).GetPerformanceAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.RejectedAssignments.Should().Be(1, "the rejection happened regardless of who holds the booking now");
        result.Value.TotalAssignments.Should().Be(1, "the rejected assignment is still an assignment this provider received");
    }

    [Fact]
    public async Task GetPerformanceAsync_counts_an_accepted_assignment()
    {
        await using var context = _database.CreateContext();
        var assignmentService = CreateAssignmentService(context);
        var bookingId = await SeedBookingAsync(context);

        (await assignmentService.AssignAsync(bookingId, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null)))
            .IsSuccess.Should().BeTrue();
        (await assignmentService.AcceptAsync(bookingId, _providerId)).IsSuccess.Should().BeTrue();

        var result = await CreateService(context).GetPerformanceAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalAssignments.Should().Be(1);
        result.Value.AcceptedAssignments.Should().Be(1);
        result.Value.RejectedAssignments.Should().Be(0);
    }

    [Fact]
    public async Task GetPerformanceAsync_does_not_count_another_providers_assignments()
    {
        await using var context = _database.CreateContext();
        var assignmentService = CreateAssignmentService(context);
        var bookingId = await SeedBookingAsync(context);

        (await assignmentService.AssignAsync(bookingId, _adminUserId, new AssignProviderRequest(_replacementProviderId, ResponseDeadline: null)))
            .IsSuccess.Should().BeTrue();

        var result = await CreateService(context).GetPerformanceAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalAssignments.Should().Be(0);
    }

    [Fact]
    public async Task GetPerformanceAsync_returns_not_found_for_an_unknown_provider()
    {
        await using var context = _database.CreateContext();

        var result = await CreateService(context).GetPerformanceAsync(Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Provider.NotFound");
    }

    public void Dispose() => _database.Dispose();
}
