using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Application.Bookings;

/// <summary>
/// Shared helpers for the two things every caller touching
/// <see cref="BookingCompletionProof"/> needs (tasks 196, 198):
/// <list type="bullet">
/// <item>the task 196 guard - both writers of <see cref="BookingStatus.Completed"/>
/// (<c>ProviderJobService.CompleteAsync</c> and
/// <c>BookingManagementService.UpdateStatusAsync</c>) call
/// <see cref="EnsureCompletionProofExistsAsync"/> so "no proof, no
/// Completed" is enforced once rather than re-implemented per caller;</item>
/// <item>the read-side mapping every one of the three surfaces (provider,
/// customer, admin - task 198) needs to show the same proof.</item>
/// </list>
/// </summary>
public static class BookingCompletionProofSupport
{
    /// <summary>Null if a completion proof exists for the booking; otherwise the business error the caller should return instead of transitioning to Completed.</summary>
    public static async Task<Error?> EnsureCompletionProofExistsAsync(this IBookingCompletionProofRepository repository, Guid bookingId)
    {
        var exists = await repository.ExistsForBookingAsync(bookingId);
        return exists
            ? null
            : Error.Business(
                "Booking.CompletionProofRequired",
                "This booking cannot be marked Completed until a completion proof (photos and checklist) has been submitted.");
    }

    public static BookingCompletionProofResponse? ToResponse(this BookingCompletionProof? proof) =>
        proof is null
            ? null
            : new BookingCompletionProofResponse(
                proof.Id,
                proof.BookingId,
                proof.PhotoRefs,
                proof.ChecklistAnswers.Select(a => new CompletionChecklistAnswerResponse(a.Item, a.Completed, a.Notes)).ToList(),
                proof.SubmittedByProviderId,
                proof.SubmittedAtUtc);

    /// <summary>Customer-facing read (task 198): 404s if the booking doesn't exist or isn't the caller's own (SRS 28.3 IDOR), null value if the booking simply has no proof yet (not every booking reaches Completed).</summary>
    public static async Task<Result<BookingCompletionProofResponse?>> GetForCustomerAsync(
        this IBookingCompletionProofRepository completionProofRepository, IBookingRepository bookingRepository, Guid customerId, Guid bookingId)
    {
        var booking = await bookingRepository.GetByIdAsync(bookingId);
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Booking.NotFound", "The specified booking does not exist.");
        }

        var proof = await completionProofRepository.GetByBookingIdAsync(bookingId);
        return proof.ToResponse();
    }

    /// <summary>Admin read (task 198, SRS 12.11.2 dispute review): no ownership check, only existence.</summary>
    public static async Task<Result<BookingCompletionProofResponse?>> GetForAdminAsync(
        this IBookingCompletionProofRepository completionProofRepository, IBookingRepository bookingRepository, Guid bookingId)
    {
        var booking = await bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("Booking.NotFound", "The specified booking does not exist.");
        }

        var proof = await completionProofRepository.GetByBookingIdAsync(bookingId);
        return proof.ToResponse();
    }
}
