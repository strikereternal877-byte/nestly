using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Interceptors;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Task 288: a provider cannot be assigned to two bookings whose slots
/// overlap in time - one person cannot be in two places at once.
///
/// The three defects these tests pin, all of which the pre-288 code shipped:
/// (a) with no ProviderCapacity row the eligibility gate returned "unlimited"
/// and nothing stopped the second assignment - and no provider has such a row
/// today, so this was every provider;
/// (b) MaxJobsPerSlot compared SlotWindowId for equality, so two windows
/// overlapping in clock time never collided;
/// (c) SlotStartTimeSnapshot/SlotEndTimeSnapshot were never compared between
/// bookings anywhere, so there was no time-overlap logic to begin with.
///
/// Overlap is half-open on purpose - see the back-to-back test.
/// </summary>
public sealed class ProviderDoubleBookingTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ProviderDoubleBookingTests(TestDatabase db) => _db = db;

    private static readonly DateOnly SlotDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
    private static readonly Guid AdminUserId = Guid.NewGuid();

    private static readonly TimeSpan NineAm = TimeSpan.FromHours(9);
    private static readonly TimeSpan TenAm = TimeSpan.FromHours(10);
    private static readonly TimeSpan ElevenAm = TimeSpan.FromHours(11);
    private static readonly TimeSpan Noon = TimeSpan.FromHours(12);
    private static readonly TimeSpan OnePm = TimeSpan.FromHours(13);
    private static readonly TimeSpan TwoPm = TimeSpan.FromHours(14);

    private sealed record Fixture(Guid CustomerId, Guid CategoryId, Guid ServiceId, Guid CityId, string PincodeCode);

    private static Fixture Seed(NestlyDbContext context)
    {
        string pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);

        context.Add(customer);
        context.States.Add(state);
        context.Cities.Add(city);
        context.Pincodes.Add(pincode);
        context.Add(category);
        context.Add(service);
        context.SaveChanges();

        return new Fixture(customer.Id, category.Id, service.Id, city.Id, pincodeCode);
    }

    private static Booking AddBooking(
        NestlyDbContext context, Fixture f, TimeSpan start, TimeSpan end, DateOnly? date = null, Guid? slotWindowId = null)
    {
        // A real SlotWindow row, not just an id: ProviderMatchingService
        // resolves the booking's window to find the city to match service
        // areas against, and returns no candidates at all without it.
        var windowId = slotWindowId ?? Guid.NewGuid();
        if (context.SlotWindows.Find(windowId) is null)
        {
            context.SlotWindows.Add(new SlotWindow(windowId, f.CityId, "Window", start, end));
        }

        var address = new AddressSnapshot(
            "Home", "221B Baker Street", null, null, f.PincodeCode, "Bengaluru", "Karnataka",
            12.9352m, 77.6245m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(windowId, date ?? SlotDate, "Window", start, end);
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);

        var booking = new Booking(Guid.NewGuid(), f.CustomerId, new CustomerSnapshot("Asha Rao", "9876543210"), null, address, slot, price);
        booking.AddItem(Guid.NewGuid(), f.ServiceId, "Deep Clean", "deep-clean", 500m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        context.Add(booking);
        context.SaveChanges();
        return booking;
    }

    private static Provider AddActiveProvider(NestlyDbContext context, Fixture f, decimal lat = 12.9352m, decimal lng = 77.6146m)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        provider.UpdateLocation(lat, lng);
        context.Add(provider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), provider.Id, f.CategoryId));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), provider.Id, f.CityId));
        // Every day of the week, wide enough to cover every slot these tests
        // use, so availability is never the reason a candidate is rejected -
        // these tests are about the overlap invariant alone.
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            context.Add(new ProviderAvailabilityWindow(Guid.NewGuid(), provider.Id, day, TimeSpan.FromHours(7), TimeSpan.FromHours(20)));
        }
        context.SaveChanges();
        return provider;
    }

    private static BookingProviderAssignmentService BuildAssignmentService(NestlyDbContext context) => new(
        new BookingRepository(context),
        new ProviderRepository(context),
        new ServiceRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions()),
        TestServices.ProviderNotificationPublisher(context),
        context);

    private static ProviderAssignmentEligibilityService BuildEligibilityService(NestlyDbContext context) => new(
        new BookingRepository(context),
        new ProviderAvailabilityWindowRepository(context),
        new ProviderBlackoutDateRepository(context),
        new ProviderCapacityRepository(context),
        new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        // Task 289 on the sandbox estimator: every booking here shares one
        // address, so every leg is zero-length and the travel check never
        // fires - including for the back-to-back case below, which stays legal
        // precisely because there is no drive to make.
        TravelFeasibilityFactory.Sandbox(context),
        context);

    // Sandbox route estimates (no HTTP, no key): these tests are about the
    // overlap invariant, and a sandbox-only response leaves task 267's ranking
    // at the straight-line ordering they were written against.
    private static ProviderAutoAssignmentHandler BuildHandler(NestlyDbContext context) => new(
        new EligibleProviderSearchService(
            new ProviderMatchingService(
                new BookingRepository(context),
                context,
                new SandboxRouteEstimateProvider(Options.Create(new SandboxRouteEstimateOptions())),
                Options.Create(new AutoAssignmentOptions())),
            BuildEligibilityService(context)),
        BuildEligibilityService(context),
        BuildAssignmentService(context),
        new BookingProviderAssignmentRepository(context),
        new BookingRepository(context),
        new RecurringPlanProviderContinuityService(new BookingRepository(context)),
        Options.Create(new AutoAssignmentOptions { RetryAttempts = 3, Enabled = true }),
        NullLogger<ProviderAutoAssignmentHandler>.Instance);

    /// <summary>
    /// Live-assigns <paramref name="provider"/> to a booking at
    /// <paramref name="start"/>-<paramref name="end"/>, then returns a second,
    /// not-yet-assigned booking at <paramref name="candidateStart"/>-<paramref name="candidateEnd"/>
    /// for the assertion to attempt. No ProviderCapacity row is ever created -
    /// that is defect (a): the state every provider is in today.
    /// </summary>
    private async Task<(Guid ProviderId, Guid ExistingBookingId, Guid CandidateBookingId)> SeedExistingJobAndCandidateAsync(
        TimeSpan start, TimeSpan end, TimeSpan candidateStart, TimeSpan candidateEnd,
        DateOnly? candidateDate = null, Guid? sharedSlotWindowId = null,
        BookingProviderAssignmentStatus existingStatus = BookingProviderAssignmentStatus.Assigned)
    {
        using var context = _db.CreateContext();
        var f = Seed(context);
        var provider = AddActiveProvider(context, f);

        var existing = AddBooking(context, f, start, end, slotWindowId: sharedSlotWindowId);
        var candidate = AddBooking(context, f, candidateStart, candidateEnd, candidateDate, sharedSlotWindowId);

        var assignment = new BookingProviderAssignment(
            Guid.NewGuid(), existing.Id, provider.Id, BookingAssignedByType.System, null, null);
        switch (existingStatus)
        {
            case BookingProviderAssignmentStatus.Assigned:
                break;
            case BookingProviderAssignmentStatus.Accepted:
                assignment.Accept();
                break;
            case BookingProviderAssignmentStatus.Rejected:
                assignment.Reject("Not available.");
                break;
            case BookingProviderAssignmentStatus.Reassigned:
                assignment.MarkReassigned(Guid.NewGuid());
                break;
            case BookingProviderAssignmentStatus.Withdrawn:
                assignment.Withdraw();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(existingStatus), existingStatus, "Unhandled assignment status.");
        }

        context.Add(assignment);
        existing.AssignProvider(provider.Id);
        await context.SaveChangesAsync();

        return (provider.Id, existing.Id, candidate.Id);
    }

    private async Task<bool> IsEligibleAsync(Guid providerId, Guid bookingId)
    {
        using var context = _db.CreateContext();
        return await BuildEligibilityService(context).IsEligibleAsync(providerId, bookingId);
    }

    // ---------------------------------------------------------------------
    // The overlap predicate itself.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Defect (a), and the single most important case in this file: the
    /// provider has no ProviderCapacity row - which today is every provider,
    /// since nothing in this codebase can create one - and the pre-288
    /// HasCapacityAsync therefore returned "unlimited" for the identical slot.
    /// </summary>
    [Fact]
    public async Task IsEligible_false_for_the_exact_same_slot_even_though_the_provider_has_no_ProviderCapacity_row()
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, NineAm, ElevenAm);

        using (var context = _db.CreateContext())
        {
            context.Set<ProviderCapacity>().Should().BeEmpty("this is the state every provider is in today - the invariant must not depend on a capacity row");
        }

        (await IsEligibleAsync(seed.ProviderId, seed.CandidateBookingId)).Should().BeFalse();
    }

    /// <summary>Defect (b): the same two overlapping slots, but under two different SlotWindowIds, which MaxJobsPerSlot's equality comparison could never catch.</summary>
    [Fact]
    public async Task IsEligible_false_for_overlapping_slots_belonging_to_different_slot_windows()
    {
        // 09:00-11:00 already live; candidate 10:00-12:00 - a different
        // window (AddBooking mints a fresh SlotWindowId per booking unless
        // told otherwise), overlapping 10:00-11:00.
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, TenAm, Noon);

        using (var context = _db.CreateContext())
        {
            var windowIds = context.Set<Booking>()
                .Where(b => b.Id == seed.ExistingBookingId || b.Id == seed.CandidateBookingId)
                .Select(b => b.SlotWindowId)
                .ToList();
            windowIds.Distinct().Should().HaveCount(2, "the point of this case is that the two overlapping slots are NOT the same slot window");
        }

        (await IsEligibleAsync(seed.ProviderId, seed.CandidateBookingId)).Should().BeFalse();
    }

    [Fact]
    public async Task IsEligible_false_when_the_new_slot_starts_inside_the_existing_one()
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, TenAm, Noon);

        (await IsEligibleAsync(seed.ProviderId, seed.CandidateBookingId)).Should().BeFalse();
    }

    [Fact]
    public async Task IsEligible_false_when_the_new_slot_ends_inside_the_existing_one()
    {
        var seed = await SeedExistingJobAndCandidateAsync(TenAm, Noon, NineAm, ElevenAm);

        (await IsEligibleAsync(seed.ProviderId, seed.CandidateBookingId)).Should().BeFalse("overlap is symmetric - it must not matter which of the two started first");
    }

    [Fact]
    public async Task IsEligible_false_when_the_new_slot_completely_contains_the_existing_one()
    {
        var seed = await SeedExistingJobAndCandidateAsync(TenAm, ElevenAm, NineAm, OnePm);

        (await IsEligibleAsync(seed.ProviderId, seed.CandidateBookingId)).Should().BeFalse();
    }

    [Fact]
    public async Task IsEligible_false_when_the_new_slot_is_completely_contained_by_the_existing_one()
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, OnePm, TenAm, ElevenAm);

        (await IsEligibleAsync(seed.ProviderId, seed.CandidateBookingId)).Should().BeFalse();
    }

    /// <summary>The reason the interval is half-open: a slot occupies [start, end), so a job ending at 11:00 and one starting at 11:00 do not overlap and both stay legal.</summary>
    [Fact]
    public async Task IsEligible_true_for_back_to_back_slots_that_only_touch_at_an_endpoint()
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, ElevenAm, OnePm);

        (await IsEligibleAsync(seed.ProviderId, seed.CandidateBookingId)).Should().BeTrue("11:00-13:00 starts exactly when 09:00-11:00 ends - that is a full day's work, not a double booking");
    }

    [Fact]
    public async Task IsEligible_true_for_the_same_clock_time_on_a_different_date()
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, NineAm, ElevenAm, candidateDate: SlotDate.AddDays(1));

        (await IsEligibleAsync(seed.ProviderId, seed.CandidateBookingId)).Should().BeTrue();
    }

    [Theory]
    [InlineData(BookingProviderAssignmentStatus.Rejected)]
    [InlineData(BookingProviderAssignmentStatus.Reassigned)]
    [InlineData(BookingProviderAssignmentStatus.Withdrawn)]
    public async Task IsEligible_true_when_the_overlapping_assignment_is_no_longer_live(BookingProviderAssignmentStatus status)
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, NineAm, ElevenAm, existingStatus: status);

        (await IsEligibleAsync(seed.ProviderId, seed.CandidateBookingId)).Should().BeTrue($"a {status} assignment is nobody's commitment any more and must not block a new one");
    }

    [Fact]
    public async Task IsEligible_false_when_the_overlapping_assignment_was_accepted()
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, TenAm, Noon, existingStatus: BookingProviderAssignmentStatus.Accepted);

        (await IsEligibleAsync(seed.ProviderId, seed.CandidateBookingId)).Should().BeFalse();
    }

    /// <summary>Re-assigning a booking to the provider already on it must not deadlock against that booking's own assignment.</summary>
    [Fact]
    public async Task A_booking_is_never_its_own_conflict_when_reassigned_to_the_same_provider()
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, TwoPm, TwoPm + TimeSpan.FromHours(1));

        using var context = _db.CreateContext();
        var result = await BuildAssignmentService(context).AssignAsync(
            seed.ExistingBookingId, AdminUserId, new AssignProviderRequest(seed.ProviderId, null));

        result.IsSuccess.Should().BeTrue("the only overlapping live assignment belongs to this very booking, and is superseded rather than collided with");
    }

    // ---------------------------------------------------------------------
    // The manual admin path.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Manual_admin_assign_fails_with_a_conflict_naming_the_booking_already_on_that_slot()
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, TenAm, Noon);

        using var context = _db.CreateContext();
        var result = await BuildAssignmentService(context).AssignAsync(
            seed.CandidateBookingId, AdminUserId, new AssignProviderRequest(seed.ProviderId, null));

        result.IsFailure.Should().BeTrue("an admin must be stopped from double-booking too - capacity limits are advisory, being in one place at a time is not");
        result.Error.Code.Should().Be("BookingProviderAssignment.ProviderDoubleBooked");
        result.Error.Type.Should().Be(Nestly.BuildingBlocks.Results.ErrorType.Conflict);
        result.Error.Message.Should().Contain(seed.ExistingBookingId.ToString(), "ops need to know which other job the provider is already on");
        result.Error.Message.Should().Contain("09:00").And.Contain("11:00");
    }

    [Fact]
    public async Task Manual_admin_assign_writes_nothing_when_it_is_refused()
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, TenAm, Noon);

        using (var context = _db.CreateContext())
        {
            var result = await BuildAssignmentService(context).AssignAsync(
                seed.CandidateBookingId, AdminUserId, new AssignProviderRequest(seed.ProviderId, null));
            result.IsFailure.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var candidate = await new BookingRepository(readContext).GetByIdAsync(seed.CandidateBookingId);
        candidate!.AssignedProviderId.Should().BeNull();
        candidate.Status.Should().Be(BookingStatus.AwaitingFulfilment);
        (await new BookingProviderAssignmentRepository(readContext).ListByBookingAsync(seed.CandidateBookingId))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Manual_admin_assign_succeeds_for_a_back_to_back_slot()
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, ElevenAm, OnePm);

        using (var context = _db.CreateContext())
        {
            var result = await BuildAssignmentService(context).AssignAsync(
                seed.CandidateBookingId, AdminUserId, new AssignProviderRequest(seed.ProviderId, null));
            result.IsSuccess.Should().BeTrue();
        }

        using var readContext = _db.CreateContext();
        var candidate = await new BookingRepository(readContext).GetByIdAsync(seed.CandidateBookingId);
        candidate!.AssignedProviderId.Should().Be(seed.ProviderId);
        candidate.Status.Should().Be(BookingStatus.Assigned);
    }

    /// <summary>The automatic path shares the same guard, so a candidate that slipped past the eligibility gate still cannot be written.</summary>
    [Fact]
    public async Task AssignBySystem_is_refused_for_an_overlapping_slot()
    {
        var seed = await SeedExistingJobAndCandidateAsync(NineAm, ElevenAm, TenAm, Noon);

        using var context = _db.CreateContext();
        var result = await BuildAssignmentService(context).AssignBySystemAsync(seed.CandidateBookingId, seed.ProviderId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("BookingProviderAssignment.ProviderDoubleBooked");
    }

    // ---------------------------------------------------------------------
    // The automatic engine, end to end.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Auto_assignment_skips_the_conflicted_provider_and_picks_the_next_eligible_one()
    {
        Fixture f;
        Provider busy, free;
        Guid candidateBookingId;

        using (var context = _db.CreateContext())
        {
            f = Seed(context);

            // The nearer provider - the one ranking would otherwise pick - is
            // already on an overlapping job.
            busy = AddActiveProvider(context, f, 12.9352m, 77.6146m);
            free = AddActiveProvider(context, f, 13.0827m, 80.2707m);

            var existing = AddBooking(context, f, NineAm, ElevenAm);
            context.Add(new BookingProviderAssignment(Guid.NewGuid(), existing.Id, busy.Id, BookingAssignedByType.System, null, null));
            existing.AssignProvider(busy.Id);

            candidateBookingId = AddBooking(context, f, TenAm, Noon).Id;
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context).Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(candidateBookingId, BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment)),
                CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(candidateBookingId);
        booking!.AssignedProviderId.Should().Be(free.Id, "the nearer candidate is already committed to an overlapping slot, so the engine must move on to the next one");
        booking.Status.Should().Be(BookingStatus.Assigned);
    }

    [Fact]
    public async Task Auto_assignment_leaves_the_booking_unassigned_when_every_candidate_is_double_booked()
    {
        Fixture f;
        Guid candidateBookingId;

        using (var context = _db.CreateContext())
        {
            f = Seed(context);
            var only = AddActiveProvider(context, f);

            var existing = AddBooking(context, f, NineAm, OnePm);
            context.Add(new BookingProviderAssignment(Guid.NewGuid(), existing.Id, only.Id, BookingAssignedByType.System, null, null));
            existing.AssignProvider(only.Id);

            candidateBookingId = AddBooking(context, f, TenAm, Noon).Id;
            context.SaveChanges();
        }

        using (var context = _db.CreateContext())
        {
            await BuildHandler(context).Handle(
                new DomainEventNotification<BookingStatusChangedEvent>(
                    new BookingStatusChangedEvent(candidateBookingId, BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment)),
                CancellationToken.None);
        }

        using var readContext = _db.CreateContext();
        var booking = await new BookingRepository(readContext).GetByIdAsync(candidateBookingId);
        booking!.AssignedProviderId.Should().BeNull();
        booking.Status.Should().Be(BookingStatus.AwaitingFulfilment, "no eligible provider is not an error - the booking waits for the manual queue");
    }
}
