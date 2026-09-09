using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Slots;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 240: BookingExpirySweepJob only sweeps PaymentPending bookings
/// past the configured expiry window, transitions them to
/// <see cref="BookingStatus.Expired"/>, and releases the slot seat each one
/// was holding - a booking still within the window, or in any other status,
/// must be left untouched.
///
/// Uses a hand-written <see cref="FakeSlotAvailabilityService"/> rather than
/// the real <c>SlotAvailabilityService</c> - ReleaseSlotAsync's own capacity
/// math is already covered by task 135c's tests and
/// <c>CancellationServiceTests</c>; what this suite proves is that the job
/// itself calls it exactly once per swept booking and never for a skipped one.
/// </summary>
public sealed class BookingExpirySweepJobTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public BookingExpirySweepJobTests(TestDatabase db) => _db = db;

    private sealed class FakeSlotAvailabilityService : ISlotAvailabilityService
    {
        public List<(Guid SlotWindowId, DateOnly Date)> Released { get; } = [];

        public Task ReleaseSlotAsync(Guid slotWindowId, DateOnly date)
        {
            Released.Add((slotWindowId, date));
            return Task.CompletedTask;
        }

        public Task<Result<SlotAvailabilityResponse>> GetAvailableSlotsAsync(Guid serviceId, Guid localityId, DateOnly date) =>
            throw new NotImplementedException();

        public Task<Result<SlotRangeResponse>> GetAvailableSlotsRangeAsync(Guid serviceId, Guid localityId, DateOnly from, DateOnly to) =>
            throw new NotImplementedException();

        public Task<Result<SlotRevalidationResponse>> RevalidateSlotAsync(Guid serviceId, Guid localityId, Guid slotWindowId, DateOnly date) =>
            throw new NotImplementedException();

        public Task<Result> ReserveSlotAsync(Guid slotWindowId, DateOnly date) =>
            throw new NotImplementedException();
    }

    private static Booking NewPaymentPendingBooking(Guid customerId, Guid slotWindowId)
    {
        var address = new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(slotWindowId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);
        var booking = new Booking(Guid.NewGuid(), customerId, new CustomerSnapshot("Asha Rao", "9876543210"), null, address, slot, price);
        booking.TransitionTo(BookingStatus.PaymentPending);
        return booking;
    }

    [Fact]
    public async Task SweepAsync_expires_a_stale_PaymentPending_booking_and_releases_its_slot()
    {
        var slotWindowId = Guid.NewGuid();
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var stale = NewPaymentPendingBooking(customer.Id, slotWindowId);

        using (var context = _db.CreateContext())
        {
            await new CustomerRepository(context).AddAsync(customer);

            var repository = new BookingRepository(context);
            await repository.AddAsync(stale);

            // Backdate past the 20-minute default expiry window - CreatedAtUtc
            // has no public setter (deliberately: it's set once, at
            // construction, same as every other snapshot timestamp in this
            // codebase), so this is the only way to simulate "created a while
            // ago" without sleeping the test.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE booking SET created_at_utc = {DateTime.UtcNow.AddMinutes(-30)} WHERE id = {stale.Id}");
        }

        var slotService = new FakeSlotAvailabilityService();

        using (var context = _db.CreateContext())
        {
            var job = new BookingExpirySweepJob(
                new BookingRepository(context),
                slotService,
                Options.Create(new BookingExpiryOptions()),
                NullLogger<BookingExpirySweepJob>.Instance);

            await job.SweepAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(stale.Id);
            reloaded!.Status.Should().Be(BookingStatus.Expired);
            reloaded.StatusHistory.Last().Reason.Should().Be("Payment was not completed within the expiry window.");
        }

        slotService.Released.Should().ContainSingle(r => r.SlotWindowId == slotWindowId);
    }

    [Fact]
    public async Task SweepAsync_leaves_a_recent_PaymentPending_booking_untouched()
    {
        var slotWindowId = Guid.NewGuid();
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var recent = NewPaymentPendingBooking(customer.Id, slotWindowId);

        using (var context = _db.CreateContext())
        {
            await new CustomerRepository(context).AddAsync(customer);
            await new BookingRepository(context).AddAsync(recent);
        }

        var slotService = new FakeSlotAvailabilityService();

        using (var context = _db.CreateContext())
        {
            var job = new BookingExpirySweepJob(
                new BookingRepository(context),
                slotService,
                Options.Create(new BookingExpiryOptions()),
                NullLogger<BookingExpirySweepJob>.Instance);

            await job.SweepAsync();
        }

        using (var context = _db.CreateContext())
        {
            var reloaded = await new BookingRepository(context).GetByIdAsync(recent.Id);
            reloaded!.Status.Should().Be(BookingStatus.PaymentPending, "it was created well within the expiry window");
        }

        slotService.Released.Should().BeEmpty();
    }
}
