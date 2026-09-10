using Nestly.Domain;

namespace Nestly.Application.Reviews;

public interface IReviewRepository
{
    Task AddAsync(Review review);

    Task<Review?> GetByBookingIdAsync(Guid bookingId);

    Task<IReadOnlyList<Review>> ListByServiceAsync(Guid serviceId);

    /// <summary>
    /// Task 293: the per-provider rating aggregate the service-scoped reads
    /// above could never answer. Null when the provider has no visible
    /// provider-scoped review, which is not the same as a rating of zero.
    ///
    /// Aggregated in the database rather than by loading the rows: a
    /// long-tenured provider accumulates thousands of reviews and the only
    /// thing the callers want is two numbers. Counts visible reviews only -
    /// a hidden review is hidden from the rating too, or moderation would be
    /// cosmetic.
    /// </summary>
    Task<ProviderRatingSummary?> GetProviderRatingAsync(Guid providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same aggregate as <see cref="GetProviderRatingAsync"/>, batched for
    /// every provider with at least one visible review, in one round trip -
    /// the performance-ranking rollup's rating column (docs/OPEN-FIXES-FEATURES.csv
    /// "Provider performance") needs every provider's rating at once, not one
    /// provider at a time. A provider with no visible review is simply absent
    /// from the result, mirroring <c>IProviderRepository.GetDisplayNamesByIdsAsync</c>'s
    /// "missing means keep your own fallback" convention.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ProviderRatingSummary>> GetProviderRatingsAsync(CancellationToken cancellationToken = default);

    /// <summary>The bare review entity for a moderation action (task 122) - no joined display fields, just the aggregate to mutate.</summary>
    Task<Review?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Persists a moderation action (hide/unhide/flag/unflag/note) made through <see cref="Review"/>'s own mutators.</summary>
    Task UpdateAsync(Review review, CancellationToken cancellationToken = default);

    /// <summary>One review joined with the customer/service/category names the moderation screen displays (task 122), or null if it does not exist.</summary>
    Task<ReviewModerationRow?> GetRowByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Filtered, paginated admin review search (SRS 12.15, task 122).</summary>
    Task<ReviewModerationSearchResult> SearchAsync(ReviewModerationCriteria criteria, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>The full (unpaginated, capped) set of rows matching an export request (SRS 12.15 "Export reviews", task 122).</summary>
    Task<IReadOnlyList<ReviewModerationRow>> ListForExportAsync(ReviewModerationCriteria criteria, CancellationToken cancellationToken = default);
}
