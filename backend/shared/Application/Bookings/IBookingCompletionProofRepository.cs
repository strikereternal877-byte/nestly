using Nestly.Domain;

namespace Nestly.Application.Bookings;

/// <summary>Persistence for <see cref="BookingCompletionProof"/> (task 195). One row per booking - see the entity's own doc comment.</summary>
public interface IBookingCompletionProofRepository
{
    Task AddAsync(BookingCompletionProof proof);

    Task UpdateAsync(BookingCompletionProof proof);

    Task<BookingCompletionProof?> GetByBookingIdAsync(Guid bookingId);

    /// <summary>Used by the task 196 completion guard - cheaper than a full load when only presence matters.</summary>
    Task<bool> ExistsForBookingAsync(Guid bookingId);

    /// <summary>Every proof still awaiting an admin verdict, oldest submission first - the completion-proof review queue (Order/Booking Management UX pass gap: previously reachable only by opening one InProgress booking at a time). Mirrors <c>ProviderKycDocumentRepository.ListPendingAsync</c>'s own unpaginated-work-queue convention.</summary>
    Task<IReadOnlyList<BookingCompletionProof>> ListPendingAsync(CancellationToken cancellationToken = default);
}
