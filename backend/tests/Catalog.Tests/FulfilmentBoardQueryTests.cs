using FluentAssertions;
using Nestly.Application;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page,
/// Fulfilment control room": <see cref="BookingRepository.ListForFulfilmentBoardAsync"/>
/// returns exactly the operationally live bookings for one calendar day
/// (Confirmed/AwaitingFulfilment/Assigned/ProviderEnRoute/ProviderArrived/
/// InProgress/Completed) and excludes everything on a different day or in a
/// terminal/not-yet-paid status (payment pending, cancelled, etc.).
/// </summary>
/// <remarks>
/// A fresh database per test, same reasoning as <c>UnassignedAtRiskQueueTests</c>:
/// this query is deliberately global (every booking on the given date, no
/// customer/tenant scope), so a booking one test leaves behind would be a
/// genuine match for the next test's assertions on exact membership.
/// </remarks>
public sealed class FulfilmentBoardQueryTests : IDisposable
{
    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// A booking slotted on <paramref name="slotDate"/>, left in
    /// <paramref name="status"/> by walking the real lifecycle (never by
    /// writing the status column directly), and optionally assigned a
    /// provider - same helper shape as <c>UnassignedAtRiskQueueTests.NewBooking</c>.
    /// </summary>
    private static Booking NewBooking(Guid customerId, DateOnly slotDate, BookingStatus status, Guid? assignedProviderId = null)
    {
        var slotStart = slotDate.ToDateTime(new TimeOnly(9, 0));
        var address = new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(Guid.NewGuid(), slotDate, "Morning", slotStart.TimeOfDay, slotStart.TimeOfDay.Add(TimeSpan.FromHours(2)));
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

    [Theory]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.AwaitingFulfilment)]
    [InlineData(BookingStatus.Assigned)]
    [InlineData(BookingStatus.InProgress)]
    [InlineData(BookingStatus.Completed)]
    public async Task A_booking_slotted_today_in_a_live_fulfilment_status_is_included(BookingStatus status)
    {
        using var context = _db.CreateContext();
        var customer = NewCustomer();
        context.Add(customer);
        var booking = NewBooking(customer.Id, Today, status);
        context.Add(booking);
        context.SaveChanges();

        var rows = await new BookingRepository(context).ListForFulfilmentBoardAsync(Today);

        rows.Should().ContainSingle(b => b.Id == booking.Id);
    }

    [Theory]
    [InlineData(BookingStatus.PaymentPending)]
    [InlineData(BookingStatus.CancelledByCustomer)]
    public async Task A_booking_slotted_today_outside_the_live_status_set_is_excluded(BookingStatus status)
    {
        using var context = _db.CreateContext();
        var customer = NewCustomer();
        context.Add(customer);
        var booking = NewBooking(customer.Id, Today, status);
        context.Add(booking);
        context.SaveChanges();

        var rows = await new BookingRepository(context).ListForFulfilmentBoardAsync(Today);

        rows.Should().NotContain(b => b.Id == booking.Id);
    }

    [Fact]
    public async Task A_live_status_booking_slotted_on_a_different_day_is_excluded()
    {
        using var context = _db.CreateContext();
        var customer = NewCustomer();
        context.Add(customer);
        var tomorrow = Today.AddDays(1);
        var booking = NewBooking(customer.Id, tomorrow, BookingStatus.Confirmed);
        context.Add(booking);
        context.SaveChanges();

        var rows = await new BookingRepository(context).ListForFulfilmentBoardAsync(Today);

        rows.Should().NotContain(b => b.Id == booking.Id);
    }

    [Fact]
    public async Task An_assigned_booking_keeps_its_assigned_provider_id_on_the_returned_row()
    {
        using var context = _db.CreateContext();
        var customer = NewCustomer();
        context.Add(customer);
        var providerId = SeedActiveProvider(context);
        var booking = NewBooking(customer.Id, Today, BookingStatus.Assigned, assignedProviderId: providerId);
        context.Add(booking);
        context.SaveChanges();

        var rows = await new BookingRepository(context).ListForFulfilmentBoardAsync(Today);

        rows.Should().ContainSingle(b => b.Id == booking.Id && b.AssignedProviderId == providerId);
    }

    private static Guid SeedActiveProvider(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        context.Add(provider);
        return provider.Id;
    }
}
