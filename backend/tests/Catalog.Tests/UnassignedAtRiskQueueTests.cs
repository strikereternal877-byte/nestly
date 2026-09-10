using FluentAssertions;
using Nestly.Application;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page,
/// Unassigned and at-risk queue": <see cref="BookingRepository.ListUnassignedAtRiskAsync"/>
/// returns exactly the paid bookings a manual assignment could still be
/// pushed onto (the same Confirmed/AwaitingFulfilment/Assigned allow-list
/// <c>BookingProviderAssignmentService.IsAssignableStatus</c> uses for an
/// admin) that have no live provider yet, soonest slot first.
/// </summary>
/// <remarks>
/// A fresh database per test, rather than the shared <c>IClassFixture&lt;TestDatabase&gt;</c>
/// most suites use - same reasoning as <c>BookingFulfilmentPromotionJobTests</c>:
/// this query is deliberately global (every unassigned, at-risk booking,
/// with no customer/tenant scope), so a booking one test leaves behind would
/// be a genuine match for the next test's assertions on exact counts/order.
/// </remarks>
public sealed class UnassignedAtRiskQueueTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A booking whose slot starts <paramref name="startsIn"/> from now, left
    /// in <paramref name="status"/> by walking the real lifecycle (never by
    /// writing the status column directly), and optionally assigned a
    /// provider - same helper shape as <c>BookingFulfilmentPromotionJobTests.NewBooking</c>.
    /// </summary>
    private static Booking NewBooking(Guid customerId, TimeSpan startsIn, BookingStatus status, Guid? assignedProviderId = null)
    {
        var slotStart = DateTime.UtcNow.Add(startsIn);
        var address = new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(slotStart), "Morning", slotStart.TimeOfDay, slotStart.TimeOfDay.Add(TimeSpan.FromHours(2)));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);

        var booking = new Booking(Guid.NewGuid(), customerId, new CustomerSnapshot("Asha Rao", "9876543210"), null, address, slot, price);
        booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Deep Cleaning", "deep-cleaning", 500m, 1);

        foreach (var step in PathTo(status))
        {
            booking.TransitionTo(step);
        }

        if (assignedProviderId.HasValue)
        {
            booking.AssignProvider(assignedProviderId.Value);
        }

        return booking;
    }

    /// <summary>The real, legal <see cref="BookingLifecycle"/> hop sequence from Initiated to <paramref name="status"/>, for statuses this suite actually needs.</summary>
    private static IReadOnlyList<BookingStatus> PathTo(BookingStatus status) => status switch
    {
        BookingStatus.Initiated => [],
        BookingStatus.PaymentPending => [BookingStatus.PaymentPending],
        BookingStatus.Confirmed => [BookingStatus.PaymentPending, BookingStatus.Confirmed],
        BookingStatus.AwaitingFulfilment => [BookingStatus.PaymentPending, BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment],
        BookingStatus.Assigned => [BookingStatus.PaymentPending, BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment, BookingStatus.Assigned],
        BookingStatus.InProgress => [BookingStatus.PaymentPending, BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment, BookingStatus.Assigned, BookingStatus.InProgress],
        BookingStatus.Completed => [BookingStatus.PaymentPending, BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment, BookingStatus.Assigned, BookingStatus.InProgress, BookingStatus.Completed],
        BookingStatus.CancelledByCustomer => [BookingStatus.PaymentPending, BookingStatus.Confirmed, BookingStatus.CancelledByCustomer],
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No path defined for this status in this test suite."),
    };

    private static Customer NewCustomer() =>
        new(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);

    [Fact]
    public async Task A_confirmed_booking_with_no_provider_is_included()
    {
        using var context = _db.CreateContext();
        var customer = NewCustomer();
        context.Add(customer);
        var booking = NewBooking(customer.Id, TimeSpan.FromHours(5), BookingStatus.Confirmed);
        context.Add(booking);
        context.SaveChanges();

        var (rows, totalCount) = await new BookingRepository(context).ListUnassignedAtRiskAsync(page: 1, pageSize: 20);

        rows.Should().ContainSingle(b => b.Id == booking.Id);
        totalCount.Should().Be(1);
    }

    [Fact]
    public async Task An_awaiting_fulfilment_booking_with_no_provider_is_included()
    {
        using var context = _db.CreateContext();
        var customer = NewCustomer();
        context.Add(customer);
        var booking = NewBooking(customer.Id, TimeSpan.FromHours(5), BookingStatus.AwaitingFulfilment);
        context.Add(booking);
        context.SaveChanges();

        var (rows, _) = await new BookingRepository(context).ListUnassignedAtRiskAsync(page: 1, pageSize: 20);

        rows.Should().Contain(b => b.Id == booking.Id);
    }

    [Fact]
    public async Task An_assignable_status_booking_that_already_has_a_live_provider_is_excluded()
    {
        using var context = _db.CreateContext();
        var customer = NewCustomer();
        context.Add(customer);
        var providerId = SeedActiveProvider(context);
        var booking = NewBooking(customer.Id, TimeSpan.FromHours(5), BookingStatus.AwaitingFulfilment, assignedProviderId: providerId);
        context.Add(booking);
        context.SaveChanges();

        var (rows, totalCount) = await new BookingRepository(context).ListUnassignedAtRiskAsync(page: 1, pageSize: 20);

        rows.Should().NotContain(b => b.Id == booking.Id);
        totalCount.Should().Be(0);
    }

    [Theory]
    [InlineData(BookingStatus.PaymentPending)]
    [InlineData(BookingStatus.InProgress)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.CancelledByCustomer)]
    public async Task A_booking_outside_the_assignable_status_allow_list_is_excluded_even_with_no_provider(BookingStatus status)
    {
        using var context = _db.CreateContext();
        var customer = NewCustomer();
        context.Add(customer);
        var provider = status == BookingStatus.InProgress ? SeedActiveProvider(context) : (Guid?)null;
        var booking = NewBooking(customer.Id, TimeSpan.FromHours(5), status, assignedProviderId: provider);
        context.Add(booking);
        context.SaveChanges();

        var (rows, totalCount) = await new BookingRepository(context).ListUnassignedAtRiskAsync(page: 1, pageSize: 20);

        rows.Should().NotContain(b => b.Id == booking.Id);
        totalCount.Should().Be(0);
    }

    private static Guid SeedActiveProvider(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        context.Add(provider);
        return provider.Id;
    }

    [Fact]
    public async Task Results_are_sorted_soonest_slot_first_regardless_of_creation_order()
    {
        using var context = _db.CreateContext();
        var customer = NewCustomer();
        context.Add(customer);

        var soon = NewBooking(customer.Id, TimeSpan.FromHours(2), BookingStatus.Confirmed);
        var later = NewBooking(customer.Id, TimeSpan.FromDays(3), BookingStatus.Confirmed);
        var soonest = NewBooking(customer.Id, TimeSpan.FromMinutes(30), BookingStatus.AwaitingFulfilment);

        // Added out of slot order on purpose - the query's own ORDER BY, not
        // insertion order, must decide the result order.
        context.Add(later);
        context.Add(soon);
        context.Add(soonest);
        context.SaveChanges();

        var (rows, _) = await new BookingRepository(context).ListUnassignedAtRiskAsync(page: 1, pageSize: 20);

        rows.Select(b => b.Id).Should().ContainInConsecutiveOrder(soonest.Id, soon.Id, later.Id);
    }

    [Fact]
    public async Task Pagination_splits_the_queue_and_total_count_reflects_every_matching_row()
    {
        using var context = _db.CreateContext();
        var customer = NewCustomer();
        context.Add(customer);

        var bookings = Enumerable.Range(1, 5)
            .Select(i => NewBooking(customer.Id, TimeSpan.FromHours(i), BookingStatus.Confirmed))
            .ToList();
        foreach (var booking in bookings)
        {
            context.Add(booking);
        }

        context.SaveChanges();

        var (firstPage, totalCount) = await new BookingRepository(context).ListUnassignedAtRiskAsync(page: 1, pageSize: 2);
        var (secondPage, _) = await new BookingRepository(context).ListUnassignedAtRiskAsync(page: 2, pageSize: 2);

        totalCount.Should().Be(5);
        firstPage.Should().HaveCount(2);
        secondPage.Should().HaveCount(2);
        firstPage.Select(b => b.Id).Should().NotIntersectWith(secondPage.Select(b => b.Id));
        firstPage.Select(b => b.Id).Should().ContainInConsecutiveOrder(bookings[0].Id, bookings[1].Id);
    }
}
