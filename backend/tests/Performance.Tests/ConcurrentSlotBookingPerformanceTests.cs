using System.Diagnostics;
using FluentAssertions;
using Nestly.Application.Bookings;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Performance.Tests;

/// <summary>
/// Task 135c: concurrent slot booking under promotion-level traffic (SRS
/// 29.1-29.2). Many independent customers - independent DbContexts and
/// connections, raced via Task.WhenAll, mirroring separate concurrent HTTP
/// requests - all try to book the same capacity-limited slot at once.
///
/// Before this task, SlotWindow.MaxBookingsPerSlot was reported to the
/// customer (SlotOptionResponse) but never enforced anywhere in
/// BookingService/BookingSummaryService - BookingConcurrencyTests.cs
/// documented this gap explicitly (see its
/// Two_different_customers_can_both_book_the_identical_slot... test, which
/// still passes unchanged because it never configures a capacity - null
/// stays "unlimited"). Task 135c added SlotCapacityRepository's atomic
/// conditional-update reservation (mirroring
/// CouponRepository.TryReserveRedemptionAsync) and wired it into
/// BookingService.CreateAsync via ISlotAvailabilityService.ReserveSlotAsync.
/// These tests are the proof: overbooking must be structurally impossible,
/// not just unlikely.
///
/// This suite proves it against SQLite (fast, no external dependency, runs
/// everywhere `dotnet test` does). <see cref="ConcurrentSlotBookingLoadTests"/>
/// proves the identical invariant against real Postgres, with real
/// concurrent connections and a recorded latency/throughput baseline - see
/// that file's doc comment for why both exist. Seeding and service wiring
/// live in <see cref="SlotBookingScenario"/>, shared by both.
/// </summary>
public sealed class ConcurrentSlotBookingPerformanceTests : IClassFixture<PerfTestDatabase>
{
    private readonly PerfTestDatabase _db;

    public ConcurrentSlotBookingPerformanceTests(PerfTestDatabase db) => _db = db;

    [Fact]
    public async Task Exactly_capacity_many_concurrent_bookings_succeed_and_the_rest_are_rejected_as_conflicts()
    {
        const int capacity = 5;
        const int concurrentCustomers = 40;

        var fixture = SlotBookingScenario.SeedSlotWithCapacity(_db.CreateContext, capacity, concurrentCustomers);

        var tasks = fixture.Customers.Select(async pair =>
        {
            using var context = _db.CreateContext();
            return await SlotBookingScenario.BuildBookingService(context)
                .CreateAsync(pair.Customer.Id, SlotBookingScenario.RequestFor(fixture, pair.Address.Id));
        });

        var stopwatch = Stopwatch.StartNew();
        Result<BookingDetailResponse>[] results = await Task.WhenAll(tasks);
        stopwatch.Stop();

        var succeeded = results.Where(r => r.IsSuccess).ToList();
        var failed = results.Where(r => r.IsFailure).ToList();

        succeeded.Should().HaveCount(capacity, "exactly capacity-many bookings must win the race, no more and no fewer");
        failed.Should().HaveCount(concurrentCustomers - capacity);
        // One code regardless of which check caught it: a loser that reads
        // availability after the last seat is gone is turned away up front,
        // one that read it while a seat was still free loses the atomic
        // reservation instead - both are the same outcome to the customer.
        failed.Should().OnlyContain(r => r.Error.Code == "Booking.SlotCapacityReached");
        failed.Should().OnlyContain(r => r.Error.Type == ErrorType.Conflict, "a capacity-exhausted slot is a 409, not a validation or business-rule failure");

        // The definitive check: no more bookings were actually persisted for
        // this slot+date than the configured capacity, regardless of what the
        // in-memory Result objects claim.
        using var readContext = _db.CreateContext();
        int persistedBookings = readContext.Bookings.Count(b => b.SlotWindowId == fixture.SlotWindowId && b.SlotDate == fixture.Date);
        persistedBookings.Should().Be(capacity, "the database must never contain more bookings for a slot+date than its configured capacity");

        var counter = readContext.Set<SlotBookingCounter>()
            .Single(c => c.SlotWindowId == fixture.SlotWindowId && c.SlotDate == fixture.Date);
        counter.BookedCount.Should().Be(capacity);

        // Soft load-characteristic assertion: 40 concurrent bookings racing
        // for one slot, resolved (correctly) in well under a second's worth
        // of headroom even serialized behind SQLite's write lock. Generous on
        // purpose - this is a regression guard against a gross slowdown, not
        // a strict benchmark. ConcurrentSlotBookingLoadTests carries the real
        // benchmark, against the real engine.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_slot_with_no_configured_capacity_accepts_unlimited_concurrent_bookings()
    {
        const int concurrentCustomers = 25;
        var fixture = SlotBookingScenario.SeedSlotWithCapacity(_db.CreateContext, capacity: null, concurrentCustomers);

        var tasks = fixture.Customers.Select(async pair =>
        {
            using var context = _db.CreateContext();
            return await SlotBookingScenario.BuildBookingService(context)
                .CreateAsync(pair.Customer.Id, SlotBookingScenario.RequestFor(fixture, pair.Address.Id));
        });

        Result<BookingDetailResponse>[] results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.IsSuccess, "MaxBookingsPerSlot null means unlimited - capacity enforcement must not kick in at all");

        using var readContext = _db.CreateContext();
        int persistedBookings = readContext.Bookings.Count(b => b.SlotWindowId == fixture.SlotWindowId && b.SlotDate == fixture.Date);
        persistedBookings.Should().Be(concurrentCustomers);
    }

    [Fact]
    public async Task Capacity_of_one_lets_exactly_one_of_many_racing_customers_win()
    {
        const int concurrentCustomers = 15;
        var fixture = SlotBookingScenario.SeedSlotWithCapacity(_db.CreateContext, capacity: 1, concurrentCustomers);

        var tasks = fixture.Customers.Select(async pair =>
        {
            using var context = _db.CreateContext();
            return await SlotBookingScenario.BuildBookingService(context)
                .CreateAsync(pair.Customer.Id, SlotBookingScenario.RequestFor(fixture, pair.Address.Id));
        });

        Result<BookingDetailResponse>[] results = await Task.WhenAll(tasks);

        results.Count(r => r.IsSuccess).Should().Be(1, "the last-seat race is the sharpest case: many customers, one winner");
        results.Count(r => r.IsFailure && r.Error.Code == "Booking.SlotCapacityReached").Should().Be(concurrentCustomers - 1);
    }
}
