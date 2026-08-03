using Nestly.Application.Bookings;
using Nestly.Domain;

namespace Nestly.Application.ProviderJobs;

/// <summary>
/// The provider-facing view of a job's status (task 149a, PROVIDER.md API
/// surface "Jobs"). Not a persisted enum - derived at read time from the
/// combination of <see cref="BookingProviderAssignment.Status"/> (the offer/
/// response) and <see cref="Booking.Status"/> (execution progress), since
/// PROVIDER.md's SCOPE BOUNDARY keeps those two concepts in separate
/// entities. See <c>ProviderJobService.ToJobStatus</c> for the mapping.
/// </summary>
public enum ProviderJobStatus
{
    /// <summary>Offered to the provider; awaiting accept/reject.</summary>
    Assigned,

    /// <summary>Accepted, not yet started (booking still Assigned).</summary>
    Accepted,

    /// <summary>The provider declined this offer.</summary>
    Rejected,

    /// <summary>Superseded by a later (re)assignment before this provider responded.</summary>
    Reassigned,

    /// <summary>Accepted and the provider has started work (booking InProgress).</summary>
    InProgress,

    /// <summary>Work finished (booking Completed).</summary>
    Completed,

    /// <summary>The booking was cancelled (by the customer or an admin) while this assignment was still outstanding or accepted - nothing left for the provider to do (task 208).</summary>
    Withdrawn
}

public sealed record ProviderJobItemResponse(string NameSnapshot, int Quantity, decimal UnitPriceSnapshot);

/// <summary>One row in the provider's job list (task 149a "list jobs").</summary>
public sealed record ProviderJobSummaryResponse(
    Guid AssignmentId,
    Guid BookingId,
    ProviderJobStatus Status,
    string CustomerNameSnapshot,
    string CustomerMobileSnapshot,
    string AddressLine1Snapshot,
    string AddressCitySnapshot,
    string AddressPincodeSnapshot,
    DateOnly SlotDate,
    TimeSpan SlotStartTimeSnapshot,
    TimeSpan SlotEndTimeSnapshot,
    decimal TotalPayableSnapshot,
    DateTime AssignedAt,
    DateTime? ResponseDeadline);

public sealed record ProviderJobSearchResponse(IReadOnlyList<ProviderJobSummaryResponse> Items);

/// <summary>One job's full detail (task 149a "get job detail").</summary>
public sealed record ProviderJobDetailResponse(
    Guid AssignmentId,
    Guid BookingId,
    ProviderJobStatus Status,
    string CustomerNameSnapshot,
    string CustomerMobileSnapshot,
    string AddressLabelSnapshot,
    string AddressLine1Snapshot,
    string? AddressLine2Snapshot,
    string? AddressLandmarkSnapshot,
    string AddressCitySnapshot,
    string AddressStateSnapshot,
    string AddressPincodeSnapshot,
    string AddressContactNameSnapshot,
    string AddressContactMobileSnapshot,
    DateOnly SlotDate,
    TimeSpan SlotStartTimeSnapshot,
    TimeSpan SlotEndTimeSnapshot,
    IReadOnlyList<ProviderJobItemResponse> Items,
    decimal TotalPayableSnapshot,
    DateTime AssignedAt,
    DateTime? RespondedAt,
    DateTime? ResponseDeadline,
    string? Notes,
    string? CompletionProofRef);

/// <summary>Provider rejects an offered job (task 149a "reject job"), same shape as the admin-facing <c>RejectAssignmentRequest</c>.</summary>
public sealed record RejectJobRequest(string? Reason);

/// <summary>Attach completion evidence to an accepted job (task 149a "upload completion proof"). <c>ProofRef</c> is a reference to an already-uploaded file (storage key/URL), matching how KYC documents are submitted.</summary>
public sealed record UploadJobCompletionProofRequest(string ProofRef);
