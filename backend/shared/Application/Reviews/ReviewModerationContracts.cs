using Nestly.Domain;

namespace Nestly.Application.Reviews;

/// <summary>
/// Filter criteria for the admin review moderation screen (SRS 12.15, task
/// 122): status, flagged, rating range, date and service/category - the
/// exact filter set the SRS lists. All fields are optional and combine with
/// AND. Paging is intentionally kept out of this type - <see cref="ReviewModerationSearchRequest"/>
/// carries it for the paged search, while CSV export (SRS 12.15 "Export
/// reviews") applies the same criteria unpaged.
/// </summary>
public sealed record ReviewModerationCriteria(
    ReviewStatus? Status,
    bool? IsFlagged,
    int? MinRating,
    int? MaxRating,
    DateTime? FromUtc,
    DateTime? ToUtc,
    Guid? ServiceId,
    Guid? CategoryId,
    Guid? CustomerId = null);

/// <summary>One review joined with the customer/service/category names the moderation screen displays - the review itself is never denormalized, only read alongside its related names.</summary>
public sealed record ReviewModerationRow(Review Review, string CustomerName, string ServiceName, Guid CategoryId, string CategoryName);

/// <summary>A page of <see cref="ReviewModerationRow"/> plus the total match count, for pagination.</summary>
public sealed record ReviewModerationSearchResult(IReadOnlyList<ReviewModerationRow> Rows, int TotalCount);

/// <summary>
/// Query-string shape of an admin review search request (task 122).
/// <see cref="CustomerId"/> has no visible filter input on the moderation
/// screen itself (SRS 12.15 lists no such filter) - it exists purely so the
/// Customer 360 view's "Reviews written" link can deep-link here scoped to
/// one customer (Provider Management UX pass gap: there was previously no
/// way anywhere in admin-web to see reviews authored by a given customer),
/// same "backend-supported, no visible input" convention as
/// CustomerSearchRequest's own RegisteredFromUtc/MinBookingCount.
/// </summary>
public sealed record ReviewModerationSearchRequest(
    ReviewStatus? Status,
    bool? IsFlagged,
    int? MinRating,
    int? MaxRating,
    DateTime? FromUtc,
    DateTime? ToUtc,
    Guid? ServiceId,
    Guid? CategoryId,
    Guid? CustomerId = null,
    int Page = 1,
    int PageSize = 20)
{
    public ReviewModerationCriteria ToCriteria() => new(Status, IsFlagged, MinRating, MaxRating, FromUtc, ToUtc, ServiceId, CategoryId, CustomerId);
}

/// <summary>One review row as shown on the admin moderation screen (SRS 12.15, task 122).</summary>
public sealed record ReviewModerationResponse(
    Guid Id,
    Guid BookingId,
    Guid CustomerId,
    string CustomerName,
    Guid ServiceId,
    string ServiceName,
    Guid CategoryId,
    string CategoryName,
    int Rating,
    string? ReviewText,
    string? IssueTags,
    ReviewStatus Status,
    bool IsFlagged,
    string? ModeratorNote,
    Guid? ModeratedByAdminUserId,
    DateTime? ModeratedAtUtc,
    DateTime CreatedAtUtc);

/// <summary>A page of admin review rows, newest first, with the total match count for pagination.</summary>
public sealed record ReviewModerationSearchResponse(
    IReadOnlyList<ReviewModerationResponse> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>
/// A moderation action's optional reason (task 122). Kept free-text like
/// <see cref="Nestly.Application.Customers.BlockCustomerRequest"/> rather than
/// a fixed reason-code list - SRS 12.15 does not define one, and inventing a
/// taxonomy now would be speculative.
/// </summary>
public sealed record ModerateReviewRequest(string? Note);
