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

    /// <summary>
    /// A real, existing Customer/Service row for the review-rating test's
    /// <see cref="Review"/> rows below to reference - <c>ReviewConfiguration</c>
    /// FK-constrains both, unlike the arbitrary ids elsewhere in this
    /// fixture that only ever flow through the domain layer (never a real
    /// EF foreign key).
    /// </summary>
    private readonly Guid _reviewCustomerId;
    private readonly Guid _serviceId;

    public ProviderPerformanceServiceTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        var replacement = new Provider(Guid.NewGuid(), "Meena Iyer", "Meena's Services", ProviderType.Individual, "+919876500000");
        provider.ChangeStatus(ProviderStatus.Active);
        replacement.ChangeStatus(ProviderStatus.Active);
        _providerId = provider.Id;
        _replacementProviderId = replacement.Id;

        var reviewCustomer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Priya Nair", CustomerStatus.Active);
        var category = new Category(Guid.NewGuid(), "Cleaning " + Guid.NewGuid().ToString("N")[..6], "cleaning-" + Guid.NewGuid().ToString("N")[..6], "Home cleaning services");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid().ToString("N")[..6], "desc", 500m);
        _reviewCustomerId = reviewCustomer.Id;
        _serviceId = service.Id;

        context.AddRange(provider, replacement, reviewCustomer, category, service);
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
            new CityRepository(context), new ServiceRepository(context), new PincodeRepository(context)),
        new ProviderAvailabilityWindowRepository(context),
        new ReviewRepository(context));

    private static BookingProviderAssignmentService CreateAssignmentService(NestlyDbContext context) => new(
        new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context), new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions()), context);

    /// <summary>
    /// <paramref name="startHour"/> defaults to the original single-booking
    /// tests' 9am slot; tests that assign more than one booking to the same
    /// provider must give each a non-overlapping window, or
    /// <c>ProviderScheduleConflictService</c> (correctly) refuses the second
    /// <c>AssignAsync</c>/<c>AcceptAsync</c> as a double-booking.
    /// </summary>
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

    private async Task<Guid> SeedBookingAsync(NestlyDbContext context, int startHour = 9)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        await context.AddAsync(customer);

        var booking = NewAwaitingFulfilmentBooking(customer.Id, startHour);
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

    /// <summary>
    /// Docs/OPEN-FIXES-FEATURES.csv "Provider performance": acceptance rate,
    /// completion rate and average rating computed from three assignments
    /// (one accepted-and-later-completed, one accepted-and-still-live, one
    /// rejected) plus two visible reviews.
    /// </summary>
    [Fact]
    public async Task GetPerformanceAsync_computes_acceptance_completion_and_rating()
    {
        await using var context = _database.CreateContext();
        var assignmentService = CreateAssignmentService(context);

        var acceptedBookingId = await SeedBookingAsync(context, startHour: 9);
        (await assignmentService.AssignAsync(acceptedBookingId, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null)))
            .IsSuccess.Should().BeTrue();
        (await assignmentService.AcceptAsync(acceptedBookingId, _providerId)).IsSuccess.Should().BeTrue();

        var completedBookingId = await SeedBookingAsync(context, startHour: 13);
        (await assignmentService.AssignAsync(completedBookingId, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null)))
            .IsSuccess.Should().BeTrue();
        (await assignmentService.AcceptAsync(completedBookingId, _providerId)).IsSuccess.Should().BeTrue();

        var rejectedBookingId = await SeedBookingAsync(context, startHour: 17);
        (await assignmentService.AssignAsync(rejectedBookingId, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null)))
            .IsSuccess.Should().BeTrue();
        (await assignmentService.RejectAsync(rejectedBookingId, new RejectAssignmentRequest("Busy"))).IsSuccess.Should().BeTrue();

        // Completing the middle job LAST, after the third booking is already
        // assigned/rejected: ProviderJobOccupancyService.EffectiveEndTime
        // extends a Completed, non-duration job's occupied window out to its
        // real CompletedAt wall-clock time (the early-release/overrun step),
        // which - completed "now", at whatever real time this suite happens
        // to run - could otherwise land past 17:00 and collide with the
        // rejected booking's slot. Bypasses ProviderJobService's
        // completion-proof/OTP workflow on purpose - this test is about the
        // aggregate math, not that unrelated flow, mirroring how
        // BookingProviderAssignment.Complete is exercised directly elsewhere
        // in this suite's sibling tests.
        var assignmentRepository = new BookingProviderAssignmentRepository(context);
        var completedAssignment = await assignmentRepository.GetCurrentByBookingAsync(completedBookingId);
        completedAssignment!.Complete(DateTime.UtcNow);
        await assignmentRepository.UpdateAsync(completedAssignment);

        var reviewRepository = new ReviewRepository(context);
        await reviewRepository.AddAsync(new Review(Guid.NewGuid(), acceptedBookingId, _reviewCustomerId, _serviceId, _providerId, rating: 4, reviewText: null));
        await reviewRepository.AddAsync(new Review(Guid.NewGuid(), completedBookingId, _reviewCustomerId, _serviceId, _providerId, rating: 5, reviewText: null));

        var result = await CreateService(context).GetPerformanceAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalAssignments.Should().Be(3, "three offers were made regardless of outcome");
        // Accepted (still live) + Completed (was accepted, then finished) both count as "said yes".
        result.Value.AcceptanceRatePercent.Should().Be(66.7, "2 of 3 offers were accepted, rounded to one decimal");
        result.Value.CompletionRatePercent.Should().Be(50.0, "1 of the 2 accepted offers finished");
        result.Value.AverageResponseTimeMinutes.Should().NotBeNull("every offer here was answered (accepted or rejected)");
        result.Value.AverageResponseTimeMinutes!.Value.Should().BeGreaterThanOrEqualTo(0);
        result.Value.AverageRating.Should().Be(4.5);
        result.Value.RatingCount.Should().Be(2);
    }

    [Fact]
    public async Task GetPerformanceAsync_leaves_rates_and_rating_null_with_no_data()
    {
        await using var context = _database.CreateContext();

        var result = await CreateService(context).GetPerformanceAsync(_providerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.AcceptanceRatePercent.Should().BeNull("no offers were ever made - not a 0% rate");
        result.Value.CompletionRatePercent.Should().BeNull("nothing was ever accepted");
        result.Value.AverageResponseTimeMinutes.Should().BeNull("nothing was ever answered");
        result.Value.AverageRating.Should().BeNull("no visible review exists yet - not a rating of zero");
        result.Value.RatingCount.Should().Be(0);
    }

    public void Dispose() => _database.Dispose();
}
