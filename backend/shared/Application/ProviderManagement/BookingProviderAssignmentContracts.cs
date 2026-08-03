using Nestly.Domain;

namespace Nestly.Application.ProviderManagement;

/// <summary>Admin assigns a provider to a booking (task 147, PROVIDER.md OPEN DECISIONS #1 - manual, admin-driven).</summary>
public sealed record AssignProviderRequest(Guid ProviderId, DateTime? ResponseDeadline);

/// <summary>Rejects the booking's current outstanding assignment (task 159). An admin may record this on the provider's behalf (e.g. a phone-call decline); the same service method is what a future provider-facing reject endpoint (task 151) would call.</summary>
public sealed record RejectAssignmentRequest(string? Reason);

public sealed record BookingProviderAssignmentResponse(
    Guid Id,
    Guid BookingId,
    Guid ProviderId,
    string ProviderDisplayName,
    BookingAssignedByType AssignedByType,
    Guid? AssignedByUserId,
    DateTime AssignedAt,
    BookingProviderAssignmentStatus Status,
    DateTime? ResponseDeadline,
    DateTime? RespondedAt,
    string? Notes,
    string? CompletionProofRef);
