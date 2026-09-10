using FluentAssertions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Row 38, docs/OPEN-FIXES-FEATURES.csv: the job-offer response window
/// (a) is now configurable with a more realistic default (see
/// AutoAssignmentOptions.ResponseWindowMinutes's own doc comment), and
/// (b) can be extended - rather than silently lost - when a provider's own
/// accept attempt fails for a reason that was not their fault, and Accept
/// itself is now idempotent for a provider retrying their own prior success.
/// </summary>
public sealed class ResponseDeadlineExtensionTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public ResponseDeadlineExtensionTests(TestDatabase db) => _db = db;

    private static readonly DateOnly SlotDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
    private static readonly Guid AdminUserId = Guid.NewGuid();

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

    private static Booking AddAwaitingFulfilmentBooking(NestlyDbContext context, Fixture f)
    {
        var windowId = Guid.NewGuid();
        context.SlotWindows.Add(new SlotWindow(windowId, f.CityId, "Window", TimeSpan.FromHours(9), TimeSpan.FromHours(11)));

        var address = new AddressSnapshot(
            "Home", "221B Baker Street", null, null, f.PincodeCode, "Bengaluru", "Karnataka",
            12.9352m, 77.6245m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(windowId, SlotDate, "Window", TimeSpan.FromHours(9), TimeSpan.FromHours(11));
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

    private static Provider AddActiveProvider(NestlyDbContext context, Fixture f)
    {
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);
        context.Add(provider);
        context.Add(new ProviderSkillMapping(Guid.NewGuid(), provider.Id, f.CategoryId));
        context.Add(new ProviderServiceArea(Guid.NewGuid(), provider.Id, f.CityId));
        context.SaveChanges();
        return provider;
    }

    private static BookingProviderAssignmentService BuildAssignmentService(NestlyDbContext context, int responseWindowMinutes = 30) => new(
        new BookingRepository(context),
        new ProviderRepository(context),
        new ServiceRepository(context),
        new BookingProviderAssignmentRepository(context),
        new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions { ResponseWindowMinutes = responseWindowMinutes }),
        context);

    private Task<(Guid BookingId, Guid ProviderId)> SeedAssignedJobAsync()
    {
        using var context = _db.CreateContext();
        var f = Seed(context);
        var bookingId = AddAwaitingFulfilmentBooking(context, f).Id;
        var providerId = AddActiveProvider(context, f).Id;
        return Task.FromResult((bookingId, providerId));
    }

    [Fact]
    public async Task ExtendResponseDeadline_pushes_an_outstanding_assignments_deadline_later()
    {
        var (bookingId, providerId) = await SeedAssignedJobAsync();

        DateTime originalDeadline;
        using (var context = _db.CreateContext())
        {
            var assignResult = await BuildAssignmentService(context, responseWindowMinutes: 5)
                .AssignBySystemAsync(bookingId, providerId);
            assignResult.IsSuccess.Should().BeTrue();
            originalDeadline = assignResult.Value.ResponseDeadline!.Value;
        }

        using (var extendContext = _db.CreateContext())
        {
            var extendResult = await BuildAssignmentService(extendContext, responseWindowMinutes: 30)
                .ExtendResponseDeadlineAsync(bookingId, providerId);

            extendResult.IsSuccess.Should().BeTrue();
            extendResult.Value.ResponseDeadline.Should().BeAfter(originalDeadline,
                "a qualifying failure must push the deadline out, not leave the countdown running");
        }
    }

    [Fact]
    public async Task ExtendResponseDeadline_is_rejected_for_a_non_owning_provider()
    {
        var (bookingId, providerId) = await SeedAssignedJobAsync();

        using var context = _db.CreateContext();
        var service = BuildAssignmentService(context);
        (await service.AssignBySystemAsync(bookingId, providerId)).IsSuccess.Should().BeTrue();

        var result = await service.ExtendResponseDeadlineAsync(bookingId, Guid.NewGuid());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("BookingProviderAssignment.NoOutstandingAssignment");
    }

    [Fact]
    public async Task AcceptAsync_is_idempotent_when_the_same_provider_retries_after_their_own_prior_success()
    {
        var (bookingId, providerId) = await SeedAssignedJobAsync();

        using var context = _db.CreateContext();
        var service = BuildAssignmentService(context);
        (await service.AssignBySystemAsync(bookingId, providerId)).IsSuccess.Should().BeTrue();

        var first = await service.AcceptAsync(bookingId, providerId);
        first.IsSuccess.Should().BeTrue();

        // Simulates a retried accept after the first call's response never
        // reached the client (dropped connection, app backgrounded) - must
        // not read as "you lost the job".
        var retry = await service.AcceptAsync(bookingId, providerId);

        retry.IsSuccess.Should().BeTrue("a provider retrying their own already-successful accept must not see AlreadyResponded");
        retry.Value.Status.Should().Be(BookingProviderAssignmentStatus.Accepted);
    }

    [Fact]
    public async Task AcceptAsync_still_fails_once_the_provider_has_already_rejected_the_job()
    {
        var (bookingId, providerId) = await SeedAssignedJobAsync();

        using var context = _db.CreateContext();
        var service = BuildAssignmentService(context);
        (await service.AssignBySystemAsync(bookingId, providerId)).IsSuccess.Should().BeTrue();

        (await service.RejectByProviderAsync(bookingId, providerId, new RejectAssignmentRequest("Not available."))).IsSuccess.Should().BeTrue();

        // A rejected assignment is no longer "active" at all (the repository
        // only ever resolves Assigned/Accepted rows as active) - real
        // terminal outcomes other than the provider's own Accepted read as
        // no outstanding assignment, not as a stale-retry success.
        var result = await service.AcceptAsync(bookingId, providerId);

        result.IsFailure.Should().BeTrue("a real terminal outcome (Rejected) must never be resurrected into a successful accept");
        result.Error.Code.Should().Be("BookingProviderAssignment.NoOutstandingAssignment");
    }
}
