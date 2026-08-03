using FluentAssertions;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderJobs;
using Nestly.Application.ProviderManagement;
using Nestly.Domain;
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
        new BookingCompletionProofRepository(context));

    private BookingProviderAssignmentService CreateAssignmentService(NestlyDbContext context) => new(
        new BookingRepository(context), new ProviderRepository(context), new BookingProviderAssignmentRepository(context));

    private static Booking NewAwaitingFulfilmentBooking(Guid customerId)
    {
        var booking = new Booking(
            Guid.NewGuid(), customerId,
            new CustomerSnapshot("Asha Rao", "9876543210"),
            null,
            new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210"),
            new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13)),
            new PriceSnapshot(999m, 1, 999m, 0m, 0m, 999m, 0m, 0m, 0m, 999m));
        booking.AddItem(Guid.NewGuid(), Guid.NewGuid(), "Deep Cleaning", "deep-cleaning", 999m, 1);
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        return booking;
    }

    /// <summary>Seeds a booking already Assigned to <see cref="_providerId"/> via a real <see cref="BookingProviderAssignmentService.AssignAsync"/> call, so the assignment row is created exactly the way task 147's admin flow creates it.</summary>
    private async Task<Guid> SeedAssignedBookingAsync(NestlyDbContext context)
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        await context.AddAsync(customer);

        var booking = NewAwaitingFulfilmentBooking(customer.Id);
        await context.AddAsync(booking);
        await context.SaveChangesAsync();

        var assignResult = await CreateAssignmentService(context).AssignAsync(
            booking.Id, _adminUserId, new AssignProviderRequest(_providerId, ResponseDeadline: null));
        assignResult.IsSuccess.Should().BeTrue();

        return booking.Id;
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

    public void Dispose() => _database.Dispose();
}
