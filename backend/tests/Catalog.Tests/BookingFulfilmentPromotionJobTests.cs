using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Abstractions.Time;
using Nestly.Application.Bookings;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 333: <c>BookingFulfilmentPromotionJob</c> is the only thing in
/// the system that performs <c>Confirmed -&gt; AwaitingFulfilment</c>, and that
/// transition is what makes tasks 246-248's automatic assignment engine run at
/// all. What matters here is that it promotes exactly the right bookings and
/// nothing else: inside the lead window yes, outside no, any status other than
/// <see cref="BookingStatus.Confirmed"/> never - a cancelled or expired booking
/// must not be dragged back into fulfilment by a background job.
///
/// The last two tests cover the properties a Hangfire job is required to have
/// rather than any product rule: re-running a pass must change nothing, and one
/// booking that cannot be written must not take the rest of the batch with it.
/// </summary>
public sealed class BookingFulfilmentPromotionJobTests : IDisposable
{
    /// <summary>
    /// A fresh database per test, rather than the <c>IClassFixture&lt;TestDatabase&gt;</c>
    /// every other suite here shares. This job's candidate query is deliberately
    /// global - "every Confirmed booking due anywhere", with no customer or
    /// tenant filter to scope it - so a booking one test leaves behind is a
    /// genuine candidate for the next test's sweep, and assertions on how many
    /// bookings a pass promoted would silently depend on test order. Isolation
    /// is what lets those counts be exact rather than "at least".
    /// </summary>
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A booking whose slot starts <paramref name="startsIn"/> from now, left in
    /// <paramref name="status"/>. Every status is reached by walking the real
    /// lifecycle rather than by writing the column directly, so a test can never
    /// set up a state <see cref="BookingLifecycle"/> would not allow.
    /// </summary>
    private static Booking NewBooking(Guid customerId, TimeSpan startsIn, BookingStatus status) =>
        NewBookingAt(customerId, DateTime.UtcNow.Add(startsIn), status);

    /// <inheritdoc cref="NewBooking"/>
    private static Booking NewBookingAt(Guid customerId, DateTime slotStart, BookingStatus status)
    {
        var address = new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(
            Guid.NewGuid(),
            DateOnly.FromDateTime(slotStart),
            "Morning",
            slotStart.TimeOfDay,
            slotStart.TimeOfDay.Add(TimeSpan.FromHours(2)));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);

        var booking = new Booking(Guid.NewGuid(), customerId, new CustomerSnapshot("Asha Rao", "9876543210"), null, address, slot, price);

        if (status == BookingStatus.Initiated)
        {
            return booking;
        }

        booking.TransitionTo(BookingStatus.PaymentPending);
        if (status == BookingStatus.PaymentPending)
        {
            return booking;
        }

        booking.TransitionTo(BookingStatus.Confirmed);
        if (status != BookingStatus.Confirmed)
        {
            booking.TransitionTo(status);
        }

        return booking;
    }

    private static Customer NewCustomer() =>
        new(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);

    private static BookingFulfilmentPromotionJob Job(
        IBookingRepository repository, IBusinessClock? clock = null, AutoAssignmentOptions? options = null) =>
        new(
            repository,
            clock ?? TestServices.Clock(),
            Options.Create(options ?? new AutoAssignmentOptions()),
            NullLogger<BookingFulfilmentPromotionJob>.Instance);

    private async Task SeedAsync(Customer customer, params Booking[] bookings)
    {
        using var context = _db.CreateContext();
        await new CustomerRepository(context).AddAsync(customer);

        var repository = new BookingRepository(context);
        foreach (var booking in bookings)
        {
            await repository.AddAsync(booking);
        }
    }

    private async Task<Booking> ReloadAsync(Guid bookingId)
    {
        using var context = _db.CreateContext();
        return (await new BookingRepository(context).GetByIdAsync(bookingId))!;
    }

    private async Task<int> RunAsync(AutoAssignmentOptions? options = null)
    {
        using var context = _db.CreateContext();
        return await Job(new BookingRepository(context), options: options).PromoteDueBookingsAsync();
    }

    [Fact]
    public async Task A_confirmed_booking_inside_the_lead_window_is_promoted_to_awaiting_fulfilment()
    {
        var customer = NewCustomer();
        var due = NewBooking(customer.Id, TimeSpan.FromHours(2), BookingStatus.Confirmed);
        await SeedAsync(customer, due);

        await RunAsync();

        var reloaded = await ReloadAsync(due.Id);
        reloaded.Status.Should().Be(BookingStatus.AwaitingFulfilment);
        reloaded.StatusHistory.Last().Reason.Should().Be(BookingFulfilmentPromotionJob.PromotionReason,
            "an admin reading the timeline has to see what moved the booking and why");
    }

    [Fact]
    public async Task A_confirmed_booking_beyond_the_lead_window_is_left_alone()
    {
        var customer = NewCustomer();
        var faraway = NewBooking(customer.Id, TimeSpan.FromDays(5), BookingStatus.Confirmed);
        await SeedAsync(customer, faraway);

        await RunAsync();

        (await ReloadAsync(faraway.Id)).Status.Should().Be(BookingStatus.Confirmed,
            "assigning five days out would read provider availability that will not still hold on the day");
    }

    /// <summary>
    /// The lead window is configuration, so widening it has to actually change
    /// which bookings are due - otherwise the switch is decorative.
    /// </summary>
    [Fact]
    public async Task Widening_the_lead_window_brings_a_further_out_booking_into_scope()
    {
        var customer = NewCustomer();
        var threeDaysOut = NewBooking(customer.Id, TimeSpan.FromDays(3), BookingStatus.Confirmed);
        await SeedAsync(customer, threeDaysOut);

        await RunAsync();
        (await ReloadAsync(threeDaysOut.Id)).Status.Should().Be(BookingStatus.Confirmed, "the default window is 24 hours");

        await RunAsync(new AutoAssignmentOptions { PromotionLeadTimeHours = 24 * 7 });
        (await ReloadAsync(threeDaysOut.Id)).Status.Should().Be(BookingStatus.AwaitingFulfilment);
    }

    /// <summary>
    /// The boundary day, pinned against a fixed clock. Two bookings on the very
    /// same slot date, two hours apart, must fall on opposite sides of a 24-hour
    /// window - this is the half of the rule the database cannot express (see
    /// <c>IBookingRepository.ListConfirmedDueForFulfilmentAsync</c>), so if the
    /// job ever quietly degraded to whole-day granularity every other test here
    /// would still pass and this one would not.
    /// </summary>
    [Fact]
    public async Task On_the_boundary_day_the_window_cuts_by_time_of_day_not_by_whole_days()
    {
        var now = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var customer = NewCustomer();

        // Cutoff is 2026-09-02 08:00, so both of these share the cutoff's date.
        var justInside = NewBookingAt(customer.Id, new DateTime(2026, 9, 2, 7, 0, 0), BookingStatus.Confirmed);
        var justOutside = NewBookingAt(customer.Id, new DateTime(2026, 9, 2, 9, 0, 0), BookingStatus.Confirmed);
        await SeedAsync(customer, justInside, justOutside);

        using (var context = _db.CreateContext())
        {
            var job = Job(new BookingRepository(context), TestServices.Clock(new FixedTimeProvider(now)));
            (await job.PromoteDueBookingsAsync()).Should().Be(1);
        }

        (await ReloadAsync(justInside.Id)).Status.Should().Be(BookingStatus.AwaitingFulfilment, "07:00 is inside a window that closes at 08:00");
        (await ReloadAsync(justOutside.Id)).Status.Should().Be(BookingStatus.Confirmed, "09:00 is an hour past it");
    }

    /// <summary>
    /// Task 358's lower bound, the mirror of the boundary-day test above. An
    /// overdue <see cref="BookingStatus.Confirmed"/> booking is still the most
    /// urgent thing the sweep can find, so it stays a candidate - but only while
    /// somebody could still be sent. Pinned against a fixed clock so the two
    /// bookings are measured against the rule, not against the moment the test
    /// happened to run.
    /// </summary>
    [Fact]
    public async Task An_overdue_booking_inside_the_lower_bound_is_still_promoted()
    {
        var now = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);
        var customer = NewCustomer();

        // Floor is 2026-09-01 08:00 at the 24-hour default, so this slot started
        // 23 hours ago - overdue, nobody assigned, still today's problem.
        var overdue = NewBookingAt(customer.Id, new DateTime(2026, 9, 1, 9, 0, 0), BookingStatus.Confirmed);
        await SeedAsync(customer, overdue);

        using (var context = _db.CreateContext())
        {
            var job = Job(new BookingRepository(context), TestServices.Clock(new FixedTimeProvider(now)));
            (await job.PromoteDueBookingsAsync()).Should().Be(1);
        }

        (await ReloadAsync(overdue.Id)).Status.Should().Be(BookingStatus.AwaitingFulfilment,
            "a booking whose slot started an hour inside the bound has nobody assigned and is the sweep's most urgent find");
    }

    /// <summary>
    /// The regression task 358 was raised for: without a lower bound, the first
    /// pass in any environment swept every past-dated Confirmed row the platform
    /// had ever accumulated into the admin's manual queue at once.
    /// </summary>
    [Fact]
    public async Task A_stale_historical_booking_past_the_lower_bound_is_left_alone()
    {
        var now = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);
        var customer = NewCustomer();

        var ancient = NewBookingAt(customer.Id, new DateTime(2026, 6, 1, 9, 0, 0), BookingStatus.Confirmed);
        await SeedAsync(customer, ancient);

        using (var context = _db.CreateContext())
        {
            var job = Job(new BookingRepository(context), TestServices.Clock(new FixedTimeProvider(now)));
            (await job.PromoteDueBookingsAsync()).Should().Be(0);
        }

        (await ReloadAsync(ancient.Id)).Status.Should().Be(BookingStatus.Confirmed,
            "no provider can be dispatched to three months ago - this is a support question, not a dispatch one");
    }

    /// <summary>
    /// The lower bound's own boundary day, pinned to the hour. Two bookings on
    /// the same slot date, two hours apart, must fall on opposite sides of the
    /// floor - the half of the rule the database cannot express, because the
    /// query can only filter to whole slot <i>dates</i>
    /// (<c>Booking.SlotStartTimeSnapshot</c> is a <c>TimeSpan</c>, whose
    /// ordering comparisons SQLite cannot translate). If the floor ever
    /// degraded to whole-day granularity, both other lower-bound tests here
    /// would still pass and this one would not.
    /// </summary>
    [Fact]
    public async Task On_the_lower_bounds_boundary_day_the_floor_cuts_by_time_of_day_not_by_whole_days()
    {
        var now = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);
        var customer = NewCustomer();

        // Floor is 2026-09-01 08:00, so both of these share the floor's date.
        var justInside = NewBookingAt(customer.Id, new DateTime(2026, 9, 1, 9, 0, 0), BookingStatus.Confirmed);
        var justOutside = NewBookingAt(customer.Id, new DateTime(2026, 9, 1, 7, 0, 0), BookingStatus.Confirmed);
        await SeedAsync(customer, justInside, justOutside);

        using (var context = _db.CreateContext())
        {
            var job = Job(new BookingRepository(context), TestServices.Clock(new FixedTimeProvider(now)));
            (await job.PromoteDueBookingsAsync()).Should().Be(1);
        }

        (await ReloadAsync(justInside.Id)).Status.Should().Be(BookingStatus.AwaitingFulfilment, "09:00 is inside a floor that opens at 08:00");
        (await ReloadAsync(justOutside.Id)).Status.Should().Be(BookingStatus.Confirmed, "07:00 is an hour before it");
    }

    [Theory]
    [InlineData(BookingStatus.Initiated)]
    [InlineData(BookingStatus.PaymentPending)]
    [InlineData(BookingStatus.CancelledByCustomer)]
    [InlineData(BookingStatus.CancelledByAdmin)]
    [InlineData(BookingStatus.Rescheduled)]
    public async Task A_booking_in_any_other_status_is_never_promoted(BookingStatus status)
    {
        var customer = NewCustomer();
        var booking = NewBooking(customer.Id, TimeSpan.FromHours(2), status);
        await SeedAsync(customer, booking);

        await RunAsync();

        (await ReloadAsync(booking.Id)).Status.Should().Be(status,
            "the candidate filter is Confirmed only - a cancelled or unpaid booking has no fulfilment to queue");
    }

    /// <summary>
    /// Hangfire retries a failed job by re-running the whole method, so a second
    /// pass over the same data has to be a no-op - not a second transition, and
    /// not a second status-history row that would make the timeline lie.
    /// </summary>
    [Fact]
    public async Task Running_the_sweep_twice_promotes_a_booking_exactly_once()
    {
        var customer = NewCustomer();
        var due = NewBooking(customer.Id, TimeSpan.FromHours(2), BookingStatus.Confirmed);
        await SeedAsync(customer, due);

        var firstPass = await RunAsync();
        var secondPass = await RunAsync();

        firstPass.Should().Be(1);
        secondPass.Should().Be(0, "the booking is no longer Confirmed, so the second pass cannot even see it");

        var reloaded = await ReloadAsync(due.Id);
        reloaded.Status.Should().Be(BookingStatus.AwaitingFulfilment);
        reloaded.StatusHistory.Count(h => h.ToStatus == BookingStatus.AwaitingFulfilment).Should().Be(1);
    }

    /// <summary>
    /// The failure-isolation requirement, and the subtler half of it. A booking
    /// whose write is rejected must cost the pass that one booking and nothing
    /// else - one bad row must not stall every dispatch on the platform until
    /// somebody notices.
    ///
    /// <para>
    /// It must also not be <i>silently promoted anyway</i>. The transition is
    /// applied in memory before anything can fail, and one unit of work saves
    /// every aggregate it holds, so without
    /// <see cref="IBookingRepository.DiscardChanges"/> the healthy booking's
    /// save flushes the failed booking's transition too - reporting a failure
    /// while committing it. The failed booking is given the earlier slot date
    /// so it is deterministically processed first and the healthy save really
    /// does come after it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task One_booking_that_cannot_be_written_does_not_abort_or_contaminate_the_rest_of_the_batch()
    {
        var customer = NewCustomer();
        var poison = NewBooking(customer.Id, TimeSpan.FromDays(-1), BookingStatus.Confirmed);
        var healthy = NewBooking(customer.Id, TimeSpan.FromHours(2), BookingStatus.Confirmed);
        await SeedAsync(customer, poison, healthy);

        using (var context = _db.CreateContext())
        {
            var repository = new ThrowingBookingRepository(new BookingRepository(context), poison.Id);

            // The lower bound is widened past the poison booking's day-old slot
            // (task 358's default would skip it before it could fail) - this
            // test is about failure isolation, and it needs the failing booking
            // to be genuinely due and sorted first.
            var promoted = await Job(repository, options: new AutoAssignmentOptions { PromotionMaxSlotAgeHours = 48 })
                .PromoteDueBookingsAsync();

            promoted.Should().Be(1);
            repository.UpdateAttempts.Should().Be(2, "the sweep must have gone on to try the second booking");
        }

        (await ReloadAsync(healthy.Id)).Status.Should().Be(BookingStatus.AwaitingFulfilment);
        (await ReloadAsync(poison.Id)).Status.Should().Be(BookingStatus.Confirmed,
            "a booking whose promotion failed stays where it was, for the next pass to retry");
    }

    [Fact]
    public async Task The_kill_switch_stops_the_sweep_promoting_anything()
    {
        var customer = NewCustomer();
        var due = NewBooking(customer.Id, TimeSpan.FromHours(2), BookingStatus.Confirmed);
        await SeedAsync(customer, due);

        var promoted = await RunAsync(new AutoAssignmentOptions { PromotionEnabled = false });

        promoted.Should().Be(0);
        (await ReloadAsync(due.Id)).Status.Should().Be(BookingStatus.Confirmed);
    }

    /// <summary>A frozen clock, so the boundary-day test measures the rule rather than the moment it happened to run.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A real <see cref="BookingRepository"/> with one booking rigged to fail on
    /// write, so the test exercises the job's own loop against real persistence
    /// rather than an entirely fake repository.
    /// </summary>
    private sealed class ThrowingBookingRepository(IBookingRepository inner, Guid failingBookingId) : IBookingRepository
    {
        public int UpdateAttempts { get; private set; }

        public Task UpdateAsync(Booking booking)
        {
            UpdateAttempts++;
            return booking.Id == failingBookingId
                ? throw new InvalidOperationException("Simulated write failure.")
                : inner.UpdateAsync(booking);
        }

        public void DiscardChanges(Booking booking) => inner.DiscardChanges(booking);

        public Task<IReadOnlyList<Booking>> ListConfirmedDueForFulfilmentAsync(DateOnly onOrAfterSlotDate, DateOnly onOrBeforeSlotDate, int skip, int take) =>
            inner.ListConfirmedDueForFulfilmentAsync(onOrAfterSlotDate, onOrBeforeSlotDate, skip, take);

        public Task AddAsync(Booking booking) => inner.AddAsync(booking);
        public Task<bool> TryAddAsync(Booking booking) => inner.TryAddAsync(booking);
        public Task<Booking?> GetByIdempotencyKeyAsync(Guid customerId, string idempotencyKey) => inner.GetByIdempotencyKeyAsync(customerId, idempotencyKey);
        public Task<Booking?> GetByIdAsync(Guid id) => inner.GetByIdAsync(id);
        public Task<IReadOnlyList<Booking>> ListByCustomerAsync(Guid customerId, IReadOnlyList<BookingStatus> statuses) => inner.ListByCustomerAsync(customerId, statuses);
        public Task<(IReadOnlyList<Booking> Rows, int TotalCount)> ListByCustomerPagedAsync(Guid customerId, IReadOnlyList<BookingStatus> statuses, int page, int pageSize) => inner.ListByCustomerPagedAsync(customerId, statuses, page, pageSize);
        public Task<BookingSearchResult> SearchAsync(BookingSearchFilter filter) => inner.SearchAsync(filter);
        public Task<IReadOnlyList<Booking>> ListByAssignedProviderAsync(Guid providerId) => inner.ListByAssignedProviderAsync(providerId);
        public Task<IReadOnlyList<Booking>> ListByRecurringPlanAsync(Guid recurringBookingPlanId) => inner.ListByRecurringPlanAsync(recurringBookingPlanId);
        public Task<int> CountCompletedByCustomerAsync(Guid customerId, Guid excludingBookingId) => inner.CountCompletedByCustomerAsync(customerId, excludingBookingId);
        public Task<int> CountCompletedByAssignedProviderAsync(Guid providerId, Guid excludingBookingId) => inner.CountCompletedByAssignedProviderAsync(providerId, excludingBookingId);
        public Task<IReadOnlyList<Booking>> ListStalePaymentPendingAsync(DateTime olderThanUtc) => inner.ListStalePaymentPendingAsync(olderThanUtc);
        public Task<IReadOnlyList<Booking>> ListSummariesByIdsAsync(IReadOnlyCollection<Guid> ids) => inner.ListSummariesByIdsAsync(ids);
        public Task<IReadOnlyList<Guid>> ListServiceIdsEverBookedAsync() => inner.ListServiceIdsEverBookedAsync();
        public Task<(IReadOnlyList<Booking> Rows, int TotalCount)> ListUnassignedAtRiskAsync(int page, int pageSize) => inner.ListUnassignedAtRiskAsync(page, pageSize);
        public Task<IReadOnlyList<Booking>> ListAwaitingPaymentAsync() => inner.ListAwaitingPaymentAsync();
    }
}
