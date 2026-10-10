using Nestly.Application.Bookings;
using Nestly.BuildingBlocks.Results;

namespace Nestly.Catalog.Tests;

/// <summary>
/// A stand-in <see cref="IRecurringOccurrenceAutoChargeJob"/> for suites that
/// build <c>BookingManagementService</c> only to exercise something else
/// (cancel, reschedule, refund, completion-proof review) - not auto-charge
/// retries, which have their own dedicated coverage in
/// <c>RecurringOccurrenceAutoChargeJobTests</c>/
/// <c>ProviderAutoAssignmentHandlerTests</c>-style focused suites. Throws if
/// ever actually called, the same "never legitimately reached by these
/// tests" contract <see cref="AlwaysEligibleProviderSearchStub"/> documents
/// for its own unrelated dependency.
/// </summary>
public sealed class NotUnderTestAutoChargeJobStub : IRecurringOccurrenceAutoChargeJob
{
    public Task ProcessDueAttemptsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Auto-charge sweeping is not under test in this suite.");

    public Task<Result<AutoChargeAttemptOutcome>> ForceAttemptAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Auto-charge forced retry is not under test in this suite.");

    public Task NotifyRetriesCancelledAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Auto-charge retry cancellation is not under test in this suite.");
}
