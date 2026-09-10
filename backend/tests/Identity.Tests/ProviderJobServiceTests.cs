using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderJobs;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
using Nestly.Domain.Events;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;
using Nestly.Infrastructure.Persistence.Repositories;
using Nestly.Infrastructure.Services;

namespace Nestly.Identity.Tests;

/// <summary>
/// The provider-facing job lifecycle (task 149a, PROVIDER.md API surface
/// "Jobs"), wired to the real <c>BookingProviderAssignment</c> bridge entity
/// (task 147) rather than the earlier 501-stub JobsController.
/// </summary>
public class ProviderJobServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Guid _providerId;
    private readonly Guid _otherProviderId;
    private readonly Guid _adminUserId = Guid.NewGuid();

    public ProviderJobServiceTests()
    {
        using var context = _database.CreateContext();
        var provider = new Provider(Guid.NewGuid(), "Ravi Kumar", "Ravi's Repairs", ProviderType.Individual, "+919876543210");
        var otherProvider = new Provider(Guid.NewGuid(), "Meena Iyer", "Meena's Services", ProviderType.Individual, "+919876500000");
        // AssignAsync (task 147) only allows assigning an Active provider.
        provider.ChangeStatus(ProviderStatus.Active);
        otherProvider.ChangeStatus(ProviderStatus.Active);
        _providerId = provider.Id;
        _otherProviderId = otherProvider.Id;
        context.AddRange(provider, otherProvider);
        context.SaveChanges();
    }

    private ProviderJobService CreateJobService(NestlyDbContext context) => new(
        new BookingRepository(context),
        new BookingProviderAssignmentRepository(context),
        CreateAssignmentService(context),
        new BookingCompletionProofRepository(context),
        new NoOpBookingEtaService(),
        new RecurringBookingPlanRepository(context), new NoOpFileStorageService(),
        TestServices.ActiveJobLimit(context), TestServices.OverrunReassignment(context),
        new PaymentTransactionRepository(context), TestServices.Clock());

    private BookingProviderAssignmentService CreateAssignmentService(NestlyDbContext context) => new(
        new BookingRepository(context), new ProviderRepository(context), new ServiceRepository(context),
        new BookingProviderAssignmentRepository(context), new ProviderScheduleConflictService(context, TestServices.Occupancy()),
        Options.Create(new AutoAssignmentOptions()), context);

    private static Booking NewAwaitingFulfilmentBooking(Guid customerId, Guid? recurringBookingPlanId = null, TimeSpan? slotStart = null, TimeSpan? slotEnd = null)
    {
        var booking = new Booking(
            Guid.NewGuid(), customerId,
            new CustomerSnapshot("Asha Rao", "9876543210"),
            null,
            new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", slotStart ?? TimeSpan.FromHours(9), slotEnd ?? TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0m, 0m, 999m, 0m, 0m, 0m, 999m),
            recurringBookingPlanId: recurringBookingPlanId);
        booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Deep Cleaning", "deep-cleaning", 999m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        return booking;
    }

    /// <summary>Seeds a booking already Assigned to <see cref="_providerId"/> via a real <see cref="BookingProviderAssignmentService.AssignAsync"/> call, so the assignment row is created exactly the way task 147's admin flow creates it. Callers assigning a second booking to the same provider must pass a non-overlapping slot - task 288's own double-booking guard refuses the assignment otherwise, before this test ever reaches the one-active-job rule it means to exercise.</summary>
    private async Task<Guid> SeedAssignedBookingAsync(NestlyDbContext context, TimeSpan? slotStart = null, TimeSpan? slotEnd = null)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        await context.AddAsync(customer);

        var booking = NewAwaitingFulfilmentBooking(customer.Id, slotStart: slotStart, slotEnd: slotEnd);
        await context.AddAsync(booking);
        await context.SaveChangesAsync();

        var assignResult = await CreateAssignmentService(context).AssignAsync(
            booking.Id, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null));
        assignResult.IsSuccess.Should().BeTrue();

        return booking.Id;
    }

    /// <summary>
    /// Seeds a plan plus a booking that carries its id (task 296's FK, set for
    /// real by the scheduler since task 297), then assigns it to
    /// <see cref="_providerId"/>.
    ///
    /// Unlike the one-off seed above, this one builds the whole
    /// geography/catalog tree the plan's foreign keys demand
    /// (RecurringBookingPlanConfiguration puts a Restrict FK on every one of
    /// customer/service/city/locality/address/slot-window). Microsoft.Data.Sqlite
    /// enables <c>PRAGMA foreign_keys</c> by default, so those constraints are
    /// live in this suite even though its <see cref="TestDatabase"/> never
    /// mentions them.
    /// </summary>
    private async Task<(Guid BookingId, RecurringBookingPlan Plan)> SeedRecurringAssignedBookingAsync(
        NestlyDbContext context, RecurringBookingRecurrenceFrequency frequency)
    {
        string pincodeCode = Guid.NewGuid().ToString("N")[..6];
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var zone = new Zone(Guid.NewGuid(), city.Id, "Central");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, pincodeCode);
        var locality = new Locality(Guid.NewGuid(), zone.Id, pincode.Id, "Koramangala");
        var address = new CustomerAddress(
            Guid.NewGuid(), customer.Id, "Home", "221B Baker Street", null, null,
            pincodeCode, "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210", true);
        address.LinkToGeography(pincode.Id, locality.Id);
        var category = new Category(Guid.NewGuid(), "Cleaning", "cleaning-" + Guid.NewGuid(), "desc");
        var service = new Service(Guid.NewGuid(), category.Id, "Deep Cleaning", "deep-clean-" + Guid.NewGuid(), "desc", 999m);
        var window = new SlotWindow(Guid.NewGuid(), city.Id, "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));

        context.AddRange(customer, state, city, zone, pincode, locality, address, category, service, window);
        await context.SaveChangesAsync();

        var plan = new RecurringBookingPlan(
            Guid.NewGuid(), customer.Id, service.Id, city.Id, locality.Id, address.Id, window.Id,
            quantity: 1,
            frequency,
            frequency == RecurringBookingRecurrenceFrequency.Monthly ? null : DayOfWeek.Tuesday,
            frequency == RecurringBookingRecurrenceFrequency.Monthly ? 11 : null,
            startDate: DateOnly.FromDateTime(DateTime.UtcNow),
            endDate: null,
            occurrenceCount: 8);
        await context.AddAsync(plan);

        var booking = NewAwaitingFulfilmentBooking(customer.Id, plan.Id);
        await context.AddAsync(booking);
        await context.SaveChangesAsync();

        var assignResult = await CreateAssignmentService(context).AssignAsync(
            booking.Id, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null));
        assignResult.IsSuccess.Should().BeTrue();

        return (booking.Id, plan);
    }

    // --- Task 300: recurring jobs are distinguishable from one-off jobs ---

    [Fact]
    public async Task ListAsync_marks_a_job_generated_by_a_recurring_plan_with_the_plans_cadence()
    {
        await using var context = _database.CreateContext();
        var (bookingId, plan) = await SeedRecurringAssignedBookingAsync(
            context, RecurringBookingRecurrenceFrequency.Biweekly);

        var result = await CreateJobService(context).ListAsync(_providerId, status: null, date: null);

        var job = result.Value.Items.Should().ContainSingle(i => i.BookingId == bookingId).Subject;
        job.RecurringBookingPlanId.Should().Be(plan.Id);
        job.RecurringFrequency.Should().Be(RecurringBookingRecurrenceFrequency.Biweekly);
    }

    [Fact]
    public async Task ListAsync_leaves_a_one_off_job_unmarked()
    {
        // The badge only means something if it is absent on an ordinary job -
        // a field that is always populated distinguishes nothing.
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);

        var result = await CreateJobService(context).ListAsync(_providerId, status: null, date: null);

        var job = result.Value.Items.Should().ContainSingle(i => i.BookingId == bookingId).Subject;
        job.RecurringBookingPlanId.Should().BeNull();
        job.RecurringFrequency.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_reads_the_cadence_live_so_a_changed_plan_is_reflected_on_an_already_generated_job()
    {
        // The reason the cadence is read through the plan id instead of being
        // copied onto the booking when the occurrence is generated. A customer
        // who moves a weekly plan to monthly must not leave the provider's
        // list advertising a weekly commitment that no longer exists.
        Guid bookingId;
        Guid planId;

        await using (var seedContext = _database.CreateContext())
        {
            (bookingId, var plan) = await SeedRecurringAssignedBookingAsync(
                seedContext, RecurringBookingRecurrenceFrequency.Weekly);
            planId = plan.Id;
        }

        await using (var editContext = _database.CreateContext())
        {
            // Rewritten directly: the plan aggregate exposes no "change
            // cadence" behaviour today (a customer cancels and re-creates), and
            // what is under test is that the job list re-reads the row rather
            // than how the row came to change.
            await editContext.Database.ExecuteSqlRawAsync(
                "UPDATE recurring_booking_plan SET frequency = 'Monthly', recurrence_day_of_week = NULL, recurrence_day_of_month = 11 WHERE id = {0}",
                planId);
        }

        await using var readContext = _database.CreateContext();
        var result = await CreateJobService(readContext).ListAsync(_providerId, status: null, date: null);

        result.Value.Items.Single(i => i.BookingId == bookingId).RecurringFrequency
            .Should().Be(RecurringBookingRecurrenceFrequency.Monthly);
    }

    [Fact]
    public async Task ListAsync_returns_every_job_ever_assigned_to_the_provider()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);

        var result = await CreateJobService(context).ListAsync(_providerId, status: null, date: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle(i => i.BookingId == bookingId && i.Status == ProviderJobStatus.Assigned);
    }

    [Fact]
    public async Task ListAsync_filters_by_status()
    {
        await using var context = _database.CreateContext();
        await SeedAssignedBookingAsync(context);

        var result = await CreateJobService(context).ListAsync(_providerId, status: ProviderJobStatus.Completed, date: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDetailAsync_rejects_a_job_belonging_to_another_provider()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);

        var result = await CreateJobService(context).GetDetailAsync(_otherProviderId, bookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderJob.NotFound");
    }

    /// <summary>
    /// Records a successfully-paid, commission-recorded transaction for a
    /// booking - the same state <see cref="PaymentWebhookService"/> leaves
    /// behind at confirmation time (task 157) - so <see cref="ProviderJobService"/>'s
    /// payout breakdown has something real to read.
    /// </summary>
    private static async Task<PaymentTransaction> SeedPaidCommissionAsync(NestlyDbContext context, Guid bookingId, decimal ratePercentage)
    {
        var booking = await context.Bookings.SingleAsync(b => b.Id == bookingId);
        var transaction = new PaymentTransaction(Guid.NewGuid(), bookingId, booking.CustomerId, booking.TotalPayableSnapshot, "INR", Guid.NewGuid().ToString());
        var attempt = transaction.StartAttempt(Guid.NewGuid(), "order_" + Guid.NewGuid());
        transaction.MarkAttemptSucceeded(attempt.Id, "pay_" + Guid.NewGuid());
        transaction.RecordCommission(ratePercentage, CommissionCalculator.Calculate(transaction.Amount, ratePercentage));

        await context.PaymentTransactions.AddAsync(transaction);
        await context.SaveChangesAsync();
        return transaction;
    }

    /// <summary>
    /// Bug fix (docs/OPEN-FIXES-FEATURES.csv "Payout figure"): provider-web
    /// was showing <c>TotalPayableSnapshot</c> - the customer's gross total
    /// including tax and platform fee - as the provider's payout, with no
    /// commission deducted anywhere. Job detail must instead expose the
    /// commission actually recorded on the booking's payment (task 157) and
    /// a net payout with it deducted.
    /// </summary>
    [Fact]
    public async Task GetDetailAsync_computes_net_payout_from_recorded_commission_not_gross_total()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);
        await SeedPaidCommissionAsync(context, bookingId, ratePercentage: 15m);

        var result = await CreateJobService(context).GetDetailAsync(_providerId, bookingId);

        result.IsSuccess.Should().BeTrue();
        var detail = result.Value;
        detail.TotalPayableSnapshot.Should().Be(999m, "the gross customer total must still be exposed, just never as the provider's earning");
        detail.CommissionAmount.Should().Be(149.85m, "15% of the 999 gross, CommissionCalculator's own rounding");
        detail.NetAmountToProvider.Should().Be(849.15m);
        detail.NetAmountToProvider.Should().BeLessThan(detail.TotalPayableSnapshot, "the payout must never equal (let alone exceed) the customer's gross total");
    }

    /// <summary>
    /// A booking with nothing payable (fully wallet/AMC covered) never gets a
    /// payment transaction at all (see <c>EscrowReleaseOnCompletionHandler</c>'s
    /// task 331 comment) - the payout breakdown must degrade to zero-and-zero
    /// rather than crash or fall back to showing the (here, zero) gross total
    /// as if it were unearned income.
    /// </summary>
    [Fact]
    public async Task GetDetailAsync_reports_zero_payout_when_no_payment_transaction_exists()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);

        var result = await CreateJobService(context).GetDetailAsync(_providerId, bookingId);

        result.IsSuccess.Should().BeTrue();
        result.Value.CommissionAmount.Should().Be(0m);
        result.Value.NetAmountToProvider.Should().Be(result.Value.TotalPayableSnapshot);
    }

    [Fact]
    public async Task ListAsync_computes_net_payout_from_recorded_commission_per_row()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);
        await SeedPaidCommissionAsync(context, bookingId, ratePercentage: 15m);

        var result = await CreateJobService(context).ListAsync(_providerId, status: null, date: null);

        result.IsSuccess.Should().BeTrue();
        var row = result.Value.Items.Should().ContainSingle().Subject;
        row.CommissionAmount.Should().Be(149.85m);
        row.NetAmountToProvider.Should().Be(849.15m);
    }

    [Fact]
    public async Task AcceptAsync_moves_the_assignment_to_Accepted()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);

        var result = await CreateJobService(context).AcceptAsync(_providerId, bookingId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ProviderJobStatus.Accepted);
    }

    [Fact]
    public async Task AcceptAsync_rejects_a_caller_who_does_not_own_the_assignment()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);

        var result = await CreateJobService(context).AcceptAsync(_otherProviderId, bookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("BookingProviderAssignment.NoOutstandingAssignment");
    }

    [Fact]
    public async Task RejectAsync_returns_the_booking_to_AwaitingFulfilment()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);

        var result = await CreateJobService(context).RejectAsync(_providerId, bookingId, new RejectJobRequest("Too far away"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ProviderJobStatus.Rejected);

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.AwaitingFulfilment);
        booking.AssignedProviderId.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_requires_the_job_to_have_been_accepted_first()
    {
        await using var context = _database.CreateContext();
        var bookingId = await SeedAssignedBookingAsync(context);

        var result = await CreateJobService(context).StartAsync(_providerId, bookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderJob.NotFound");
    }

    [Fact]
    public async Task Full_lifecycle_accept_start_complete_and_upload_proof_succeeds()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);

        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var started = await service.StartAsync(_providerId, bookingId);
        started.IsSuccess.Should().BeTrue();
        started.Value.Status.Should().Be(ProviderJobStatus.InProgress);

        var submittedProof = await service.SubmitCompletionProofAsync(
            _providerId, bookingId,
            new SubmitCompletionProofRequest(["s3://proofs/job-photo.jpg"], [new CompletionChecklistAnswerRequest("Area cleaned", true, null)]));
        submittedProof.IsSuccess.Should().BeTrue();

        var completed = await service.CompleteAsync(_providerId, bookingId);
        completed.IsSuccess.Should().BeTrue();
        completed.Value.Status.Should().Be(ProviderJobStatus.Completed);

        var proof = await service.UploadCompletionProofAsync(_providerId, bookingId, new UploadJobCompletionProofRequest("s3://proofs/photo.jpg"));
        proof.IsSuccess.Should().BeTrue();
        proof.Value.CompletionProofRef.Should().Be("s3://proofs/photo.jpg");

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.Completed);
    }

    // --- Task 270: en-route / arrived ---
    //
    // TestDatabase registers no DomainEventDispatchInterceptor, so a saved
    // aggregate keeps its raised events instead of having them cleared on
    // SaveChanges. That is what lets the "exactly once" assertions below count
    // events on the booking the repository hands back. It is a divergence from
    // the running app (which does dispatch and clear); the events themselves
    // are raised by Booking.TransitionTo either way.

    [Fact]
    public async Task MarkEnRouteAsync_moves_an_accepted_job_to_ProviderEnRoute()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var result = await service.MarkEnRouteAsync(_providerId, bookingId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ProviderJobStatus.EnRoute);

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.ProviderEnRoute);
    }

    [Fact]
    public async Task MarkArrivedAsync_moves_an_en_route_job_to_ProviderArrived()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkEnRouteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var result = await service.MarkArrivedAsync(_providerId, bookingId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(ProviderJobStatus.Arrived);

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.ProviderArrived);
    }

    [Fact]
    public async Task MarkEnRouteAsync_rejects_a_provider_who_is_not_on_the_live_assignment()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var result = await service.MarkEnRouteAsync(_otherProviderId, bookingId);

        result.IsFailure.Should().BeTrue();
        // NotFound, not Forbidden: a provider with no assignment on this
        // booking must not learn that it exists (SRS 28.3 IDOR) - same answer
        // StartAsync gives.
        result.Error.Code.Should().Be("ProviderJob.NotFound");

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.Assigned);
    }

    [Fact]
    public async Task MarkArrivedAsync_rejects_a_provider_who_is_not_on_the_live_assignment()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkEnRouteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var result = await service.MarkArrivedAsync(_otherProviderId, bookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderJob.NotFound");

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.ProviderEnRoute);
    }

    [Fact]
    public async Task MarkEnRouteAsync_rejects_an_assignment_that_has_not_been_accepted_yet()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        // Assigned but never accepted - the booking sits in Assigned both
        // before and after the provider answers the offer, so the booking
        // status alone would wrongly let this through.
        var bookingId = await SeedAssignedBookingAsync(context);

        var result = await service.MarkEnRouteAsync(_providerId, bookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderJob.NotFound");

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.Assigned);
    }

    [Fact]
    public async Task MarkArrivedAsync_rejects_an_assignment_that_has_not_been_accepted_yet()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);

        var result = await service.MarkArrivedAsync(_providerId, bookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderJob.NotFound");
    }

    [Fact]
    public async Task MarkEnRouteAsync_re_tapped_while_already_en_route_succeeds_without_transitioning_again()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkEnRouteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var reTap = await service.MarkEnRouteAsync(_providerId, bookingId);

        reTap.IsSuccess.Should().BeTrue();
        reTap.Value.Status.Should().Be(ProviderJobStatus.EnRoute);

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.ProviderEnRoute);
        booking.StatusHistory.Where(h => h.ToStatus == BookingStatus.ProviderEnRoute).Should().ContainSingle();
    }

    [Fact]
    public async Task MarkArrivedAsync_re_tapped_while_already_arrived_succeeds_without_transitioning_again()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkEnRouteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkArrivedAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var reTap = await service.MarkArrivedAsync(_providerId, bookingId);

        reTap.IsSuccess.Should().BeTrue();
        reTap.Value.Status.Should().Be(ProviderJobStatus.Arrived);

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.ProviderArrived);
        booking.StatusHistory.Where(h => h.ToStatus == BookingStatus.ProviderArrived).Should().ContainSingle();
    }

    [Fact]
    public async Task MarkEnRouteAsync_raises_ProviderEnRouteEvent_exactly_once_even_when_re_tapped()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        (await service.MarkEnRouteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkEnRouteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        // Booking.TransitionTo raises this (task 272); the service must not
        // raise it a second time, and the re-tap must not transition again.
        var raised = booking!.DomainEvents.OfType<ProviderEnRouteEvent>().Should().ContainSingle().Subject;
        raised.BookingId.Should().Be(bookingId);
        raised.ProviderId.Should().Be(_providerId);
    }

    [Fact]
    public async Task MarkArrivedAsync_raises_ProviderArrivedEvent_exactly_once_even_when_re_tapped()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkEnRouteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        (await service.MarkArrivedAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkArrivedAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        var raised = booking!.DomainEvents.OfType<ProviderArrivedEvent>().Should().ContainSingle().Subject;
        raised.BookingId.Should().Be(bookingId);
        raised.ProviderId.Should().Be(_providerId);
    }

    [Fact]
    public async Task StartAsync_still_works_straight_from_Assigned_without_en_route_or_arrived()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var started = await service.StartAsync(_providerId, bookingId);

        started.IsSuccess.Should().BeTrue();
        started.Value.Status.Should().Be(ProviderJobStatus.InProgress);

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.InProgress);
        booking.DomainEvents.OfType<ProviderEnRouteEvent>().Should().BeEmpty();
        booking.DomainEvents.OfType<ProviderArrivedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_is_rejected_while_the_provider_is_only_en_route()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkEnRouteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var started = await service.StartAsync(_providerId, bookingId);

        // Task 264 deliberately left ProviderEnRoute -> InProgress off the
        // transition table: a provider passes through Arrived. Once en route,
        // arrival is the only way on.
        started.IsFailure.Should().BeTrue();
        started.Error.Code.Should().Be("ProviderJob.InvalidTransition");

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.ProviderEnRoute);
    }

    [Fact]
    public async Task MarkEnRouteAsync_is_rejected_once_the_provider_has_already_arrived()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkEnRouteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkArrivedAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var result = await service.MarkEnRouteAsync(_providerId, bookingId);

        // Idempotency forgives a no-op re-tap, not a step backwards.
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderJob.InvalidTransition");
    }

    [Fact]
    public async Task Full_lifecycle_through_en_route_and_arrived_reaches_Completed()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);

        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkEnRouteAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkArrivedAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.StartAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.SubmitCompletionProofAsync(
            _providerId, bookingId, new SubmitCompletionProofRequest(["s3://proofs/job-photo.jpg"], []))).IsSuccess.Should().BeTrue();

        var completed = await service.CompleteAsync(_providerId, bookingId);

        completed.IsSuccess.Should().BeTrue();
        completed.Value.Status.Should().Be(ProviderJobStatus.Completed);
    }

    [Fact]
    public async Task CompleteAsync_requires_the_job_to_have_been_started_first()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var result = await service.CompleteAsync(_providerId, bookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderJob.NotStarted");
    }

    [Fact]
    public async Task CompleteAsync_is_rejected_without_a_completion_proof_on_file()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.StartAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var result = await service.CompleteAsync(_providerId, bookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Booking.CompletionProofRequired");

        var booking = await new BookingRepository(context).GetByIdAsync(bookingId);
        booking!.Status.Should().Be(BookingStatus.InProgress);
    }

    [Fact]
    public async Task SubmitCompletionProofAsync_resubmission_replaces_the_previous_evidence()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);
        var bookingId = await SeedAssignedBookingAsync(context);
        (await service.AcceptAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();
        (await service.StartAsync(_providerId, bookingId)).IsSuccess.Should().BeTrue();

        var first = await service.SubmitCompletionProofAsync(
            _providerId, bookingId, new SubmitCompletionProofRequest(["s3://proofs/first.jpg"], []));
        first.IsSuccess.Should().BeTrue();

        var second = await service.SubmitCompletionProofAsync(
            _providerId, bookingId,
            new SubmitCompletionProofRequest(["s3://proofs/first.jpg", "s3://proofs/second.jpg"], [new CompletionChecklistAnswerRequest("Area cleaned", true, "Extra photo added")]));
        second.IsSuccess.Should().BeTrue();
        second.Value.Id.Should().Be(first.Value.Id);
        second.Value.PhotoRefs.Should().HaveCount(2);
        second.Value.ChecklistAnswers.Should().ContainSingle(a => a.Item == "Area cleaned" && a.Completed);

        var completed = await service.CompleteAsync(_providerId, bookingId);
        completed.IsSuccess.Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // Provider-queue model: the one-active-job rule
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_refuses_when_the_provider_has_another_job_already_in_progress()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);

        var firstBookingId = await SeedAssignedBookingAsync(context, TimeSpan.FromHours(9), TimeSpan.FromHours(11));
        (await service.AcceptAsync(_providerId, firstBookingId)).IsSuccess.Should().BeTrue();
        (await service.StartAsync(_providerId, firstBookingId)).IsSuccess.Should().BeTrue();

        // A later, non-overlapping slot - task 288's double-booking guard would
        // otherwise refuse the assignment before this test ever reached the
        // one-active-job rule it means to exercise.
        var secondBookingId = await SeedAssignedBookingAsync(context, TimeSpan.FromHours(14), TimeSpan.FromHours(16));
        (await service.AcceptAsync(_providerId, secondBookingId)).IsSuccess.Should().BeTrue();

        var result = await service.StartAsync(_providerId, secondBookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderJob.AnotherJobActive");
    }

    [Fact]
    public async Task MarkEnRouteAsync_refuses_when_the_provider_has_another_job_already_active()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);

        var firstBookingId = await SeedAssignedBookingAsync(context, TimeSpan.FromHours(9), TimeSpan.FromHours(11));
        (await service.AcceptAsync(_providerId, firstBookingId)).IsSuccess.Should().BeTrue();
        (await service.MarkEnRouteAsync(_providerId, firstBookingId)).IsSuccess.Should().BeTrue();

        var secondBookingId = await SeedAssignedBookingAsync(context, TimeSpan.FromHours(14), TimeSpan.FromHours(16));
        (await service.AcceptAsync(_providerId, secondBookingId)).IsSuccess.Should().BeTrue();

        var result = await service.MarkEnRouteAsync(_providerId, secondBookingId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderJob.AnotherJobActive");
    }

    [Fact]
    public async Task StartAsync_succeeds_once_the_providers_other_job_has_been_completed()
    {
        await using var context = _database.CreateContext();
        var service = CreateJobService(context);

        var firstBookingId = await SeedAssignedBookingAsync(context, TimeSpan.FromHours(9), TimeSpan.FromHours(11));
        (await service.AcceptAsync(_providerId, firstBookingId)).IsSuccess.Should().BeTrue();
        (await service.StartAsync(_providerId, firstBookingId)).IsSuccess.Should().BeTrue();
        (await service.SubmitCompletionProofAsync(
            _providerId, firstBookingId, new SubmitCompletionProofRequest(["s3://proofs/first.jpg"], []))).IsSuccess.Should().BeTrue();
        (await service.CompleteAsync(_providerId, firstBookingId)).IsSuccess.Should().BeTrue();

        var secondBookingId = await SeedAssignedBookingAsync(context, TimeSpan.FromHours(14), TimeSpan.FromHours(16));
        (await service.AcceptAsync(_providerId, secondBookingId)).IsSuccess.Should().BeTrue();

        // The one-active-job rule only ever blocks a *second* active job - once
        // the first is verified-complete, it no longer counts (the same
        // release the schedule-conflict/travel-feasibility checks apply).
        var result = await service.StartAsync(_providerId, secondBookingId);

        result.IsSuccess.Should().BeTrue();
    }

    public void Dispose() => _database.Dispose();
}
