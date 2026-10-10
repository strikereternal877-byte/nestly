using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers the assignment-response-expiry sweep: <see cref="BookingProviderAssignmentService.AssignBySystemAsync"/>
/// setting a real deadline, <see cref="AssignmentResponseExpirySweepJob"/>
/// finding and expiring only what has actually passed it, and
/// <see cref="BookingProviderAssignmentService.ExpireAsync"/>'s race-safety
/// no-op when an assignment already moved on. The reassignment-onward
/// behaviour (excluding the expired provider, picking the next eligible one)
/// is covered by <see cref="ProviderAutoAssignmentHandlerTests.Handle_excludes_a_provider_whose_assignment_expired_and_assigns_the_next_one"/> -
/// this suite is scoped to the sweep's own detection logic.
/// </summary>
public sealed class AssignmentResponseExpirySweepJobTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public AssignmentResponseExpirySweepJobTests(TestDatabase db) => _db = db;

    private static readonly DateOnly SlotDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));

    private sealed record Fixture(Guid BookingId, Guid ProviderId);

    private static Fixture Seed(Nestly.Infrastructure.Persistence.NestlyDbContext context)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Clean", "deep-clean-" + Guid.NewGuid(), "desc", 500m);
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+9198" + Guid.NewGuid().ToString("N")[..8]);
        provider.ChangeStatus(ProviderStatus.Active);

        context.Add(customer);
        context.Add(category);
        context.Add(service);
        context.Add(provider);

        var address = new AddressSnapshot(
            "Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka",
            12.9352m, 77.6245m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(Guid.NewGuid(), SlotDate, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);
        var booking = new Booking(Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null, address, slot, price);
        booking.AddItem(Guid.NewGuid(), service.Id, service.Name, service.Slug, 500m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        context.Add(booking);
        context.SaveChanges();

        return new Fixture(booking.Id, provider.Id);
    }

    private static BookingProviderAssignmentService BuildAssignmentService(Nestly.Infrastructure.Persistence.NestlyDbContext context, int responseWindowMinutes = 15) => new(
        new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context),
        new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions { ResponseWindowMinutes = responseWindowMinutes }), TestServices.ProviderNotificationPublisher(context), context);

    private static AssignmentResponseExpirySweepJob BuildSweepJob(Nestly.Infrastructure.Persistence.NestlyDbContext context) => new(
        new BookingProviderAssignmentRepository(context),
        BuildAssignmentService(context),
        NullLogger<AssignmentResponseExpirySweepJob>.Instance);

    [Fact]
    public async Task AssignBySystemAsync_sets_a_real_response_deadline_from_the_configured_window()
    {
        Fixture f;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
        }

        BookingProviderAssignmentResponse assignment;
        using (var context = _db.CreateContext())
        {
            var result = await BuildAssignmentService(context, responseWindowMinutes: 15).AssignBySystemAsync(f.BookingId, f.ProviderId);
            result.IsSuccess.Should().BeTrue();
            assignment = result.Value;
        }

        assignment.ResponseDeadline.Should().NotBeNull("a system assignment must carry a real deadline, not the old always-null behaviour");
        assignment.ResponseDeadline!.Value.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task SweepAsync_expires_only_assignments_past_their_deadline()
    {
        Fixture pastDue, notYetDue;
        using (var context = _db.CreateContext())
        {
            pastDue = Seed(context);
            notYetDue = Seed(context);
        }

        Guid pastDueAssignmentId, notYetDueAssignmentId;
        using (var context = _db.CreateContext())
        {
            var service = BuildAssignmentService(context);
            pastDueAssignmentId = (await service.AssignBySystemAsync(pastDue.BookingId, pastDue.ProviderId)).Value.Id;
            notYetDueAssignmentId = (await service.AssignBySystemAsync(notYetDue.BookingId, notYetDue.ProviderId)).Value.Id;
        }

        // Back-date only the first assignment's deadline directly in the
        // store - the sweep must find this one and leave the other (whose
        // real 15-minute window has obviously not passed in a test run)
        // untouched.
        using (var context = _db.CreateContext())
        {
            var stale = await context.BookingProviderAssignments.FindAsync(pastDueAssignmentId);
            typeof(BookingProviderAssignment).GetProperty(nameof(BookingProviderAssignment.ResponseDeadline))!
                .SetValue(stale, DateTime.UtcNow.AddMinutes(-1));
            await context.SaveChangesAsync();
        }

        using (var context = _db.CreateContext())
        {
            await BuildSweepJob(context).SweepAsync();
        }

        using var readContext = _db.CreateContext();
        var repository = new BookingProviderAssignmentRepository(readContext);

        (await repository.GetByIdAsync(pastDueAssignmentId))!.Status.Should().Be(BookingProviderAssignmentStatus.Expired);
        (await repository.GetByIdAsync(notYetDueAssignmentId))!.Status.Should().Be(
            BookingProviderAssignmentStatus.Assigned, "its response window has not actually passed yet");

        var bookingRepository = new BookingRepository(readContext);
        (await bookingRepository.GetByIdAsync(pastDue.BookingId))!.Status.Should().Be(
            BookingStatus.AwaitingFulfilment, "an expired assignment must return the booking to the assignable pool");
        (await bookingRepository.GetByIdAsync(notYetDue.BookingId))!.Status.Should().Be(BookingStatus.Assigned);
    }

    [Fact]
    public async Task ExpireAsync_is_a_no_op_when_the_provider_already_accepted()
    {
        Fixture f;
        using (var context = _db.CreateContext())
        {
            f = Seed(context);
        }

        Guid assignmentId;
        using (var context = _db.CreateContext())
        {
            var service = BuildAssignmentService(context);
            assignmentId = (await service.AssignBySystemAsync(f.BookingId, f.ProviderId)).Value.Id;
            (await service.AcceptAsync(f.BookingId, f.ProviderId)).IsSuccess.Should().BeTrue();
        }

        using (var context = _db.CreateContext())
        {
            // The race the sweep must lose gracefully: the provider accepted
            // in the gap between the sweep's list query and reaching this row.
            var result = await BuildAssignmentService(context).ExpireAsync(assignmentId);
            result.IsSuccess.Should().BeTrue();
            result.Value.Status.Should().Be(BookingProviderAssignmentStatus.Accepted, "an already-answered assignment must not be overwritten to Expired");
        }
    }
}
