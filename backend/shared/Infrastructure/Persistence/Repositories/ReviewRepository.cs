using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Application.Reviews;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ReviewRepository : IReviewRepository
{
    /// <summary>Hard cap on unpaginated CSV export rows (SRS 12.15 "Export reviews", task 122) - large enough for any realistic filtered export, small enough to keep the request bounded.</summary>
    private const int MaxExportRows = 10_000;

    private readonly NestlyDbContext _context;

    public ReviewRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(Review review)
    {
        await _context.Reviews.AddAsync(review);
        await _context.SaveChangesAsync();
    }

    public Task<Review?> GetByBookingIdAsync(Guid bookingId) =>
        _context.Reviews.FirstOrDefaultAsync(r => r.BookingId == bookingId);

    public async Task<IReadOnlyList<Review>> ListByServiceAsync(Guid serviceId) =>
        await _context.Reviews
            .Where(r => r.ServiceId == serviceId && r.Status == ReviewStatus.Visible)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToListAsync();

    /// <inheritdoc/>
    public async Task<ProviderRatingSummary?> GetProviderRatingAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        // GroupBy over a constant key so Average and Count come back from one
        // aggregate query, and an empty set yields no row at all rather than
        // an average over nothing (which is what makes "no rating yet"
        // expressible without a second COUNT round trip).
        var aggregate = await _context.Reviews
            .AsNoTracking()
            .Where(r => r.ProviderId == providerId && r.Status == ReviewStatus.Visible)
            .GroupBy(_ => 1)
            .Select(g => new { Average = g.Average(r => (double)r.Rating), Count = g.Count() })
            .FirstOrDefaultAsync(cancellationToken);

        return aggregate is null
            ? null
            : new ProviderRatingSummary(providerId, Math.Round(aggregate.Average, 1), aggregate.Count);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<Guid, ProviderRatingSummary>> GetProviderRatingsAsync(CancellationToken cancellationToken = default)
    {
        var aggregates = await _context.Reviews
            .AsNoTracking()
            .Where(r => r.ProviderId != null && r.Status == ReviewStatus.Visible)
            .GroupBy(r => r.ProviderId!.Value)
            .Select(g => new { ProviderId = g.Key, Average = g.Average(r => (double)r.Rating), Count = g.Count() })
            .ToListAsync(cancellationToken);

        return aggregates.ToDictionary(
            a => a.ProviderId,
            a => new ProviderRatingSummary(a.ProviderId, Math.Round(a.Average, 1), a.Count));
    }

    public Task<Review?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Reviews.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task UpdateAsync(Review review, CancellationToken cancellationToken = default)
    {
        _context.Reviews.Update(review);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ReviewModerationRow?> GetRowByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var joined = await JoinNames(_context.Reviews.Where(r => r.Id == id)).FirstOrDefaultAsync(cancellationToken);
        return joined is null ? null : ToRow(joined);
    }

    public async Task<ReviewModerationSearchResult> SearchAsync(
        ReviewModerationCriteria criteria, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var filtered = ApplyFilters(_context.Reviews, criteria);

        // Counted before the join - the count of matching reviews does not
        // depend on the joined display names, and this keeps it a single
        // simple query instead of counting through three extra joins.
        int totalCount = await filtered.CountAsync(cancellationToken);

        // Ordering/paging happens here, on the plain Review query, before the
        // join - see JoinNames's remarks for why any further per-row operator
        // (OrderBy/Where) applied after the join's projection cannot be
        // translated. This also means the join only ever runs over the
        // already-paged rows, not the whole filtered set.
        var pagedReviews = filtered
            .OrderByDescending(r => r.CreatedAtUtc)
            .ApplyPaging(page, pageSize);

        var joined = await JoinNames(pagedReviews).ToListAsync(cancellationToken);

        // SQL joins do not guarantee preserving the driving query's row order -
        // re-sorting the already-paged (small) in-memory list is cheap and
        // guarantees the newest-first order callers expect.
        var rows = joined.Select(ToRow).OrderByDescending(r => r.Review.CreatedAtUtc).ToList();

        return new ReviewModerationSearchResult(rows, totalCount);
    }

    public async Task<IReadOnlyList<ReviewModerationRow>> ListForExportAsync(
        ReviewModerationCriteria criteria, CancellationToken cancellationToken = default)
    {
        var cappedReviews = ApplyFilters(_context.Reviews, criteria)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(MaxExportRows);

        var joined = await JoinNames(cappedReviews).ToListAsync(cancellationToken);

        return joined.Select(ToRow).OrderByDescending(r => r.Review.CreatedAtUtc).ToList();
    }

    /// <summary>
    /// Every filter is applied directly against <see cref="Review"/> columns
    /// (task 122's status/flagged/rating-range/date/service filters) or, for
    /// category, a correlated <c>Any</c> subquery against <see cref="Service"/>
    /// (since <see cref="Review"/> itself has no category id) - deliberately
    /// kept at the raw-entity level, before any join or projection. Composing
    /// a further <c>Where</c> on top of a query already projected into a
    /// custom type risks EF Core inlining the two into a single "member
    /// access on a `new` expression" shape it cannot always translate (see
    /// <see cref="JoinNames"/>'s remarks) - filtering first and joining/
    /// projecting only once, last, avoids that entirely.
    /// </summary>
    private IQueryable<Review> ApplyFilters(IQueryable<Review> query, ReviewModerationCriteria criteria)
    {
        if (criteria.Status.HasValue)
        {
            query = query.Where(r => r.Status == criteria.Status.Value);
        }

        if (criteria.IsFlagged.HasValue)
        {
            query = query.Where(r => r.IsFlagged == criteria.IsFlagged.Value);
        }

        if (criteria.MinRating.HasValue)
        {
            query = query.Where(r => r.Rating >= criteria.MinRating.Value);
        }

        if (criteria.MaxRating.HasValue)
        {
            query = query.Where(r => r.Rating <= criteria.MaxRating.Value);
        }

        if (criteria.FromUtc.HasValue)
        {
            query = query.Where(r => r.CreatedAtUtc >= criteria.FromUtc.Value);
        }

        if (criteria.ToUtc.HasValue)
        {
            query = query.Where(r => r.CreatedAtUtc <= criteria.ToUtc.Value);
        }

        if (criteria.ServiceId.HasValue)
        {
            query = query.Where(r => r.ServiceId == criteria.ServiceId.Value);
        }

        if (criteria.CategoryId.HasValue)
        {
            Guid categoryId = criteria.CategoryId.Value;
            query = query.Where(r => _context.Set<Service>().Any(s => s.Id == r.ServiceId && s.CategoryId == categoryId));
        }

        return query;
    }

    /// <summary>
    /// Joins the already-filtered reviews with the customer/service/category
    /// entities the moderation screen displays (task 122). Category/Service/
    /// Customer have no navigation properties from <see cref="Review"/> (it
    /// only stores their ids, matching the rest of the codebase's convention
    /// of id-only cross-aggregate references - see <see cref="ReviewConfiguration"/>),
    /// so the join is expressed explicitly here rather than via <c>Include</c>.
    /// </summary>
    /// <remarks>
    /// The final <c>Select</c> below produces <see cref="ReviewJoinRow"/> -
    /// the four raw entities, nothing computed - and every caller either
    /// materializes it immediately (<c>FirstOrDefaultAsync</c>) or only adds
    /// further composable operators (<c>OrderBy</c>/<c>Skip</c>/<c>Take</c>)
    /// that do not reference the projected shape's members. The actual
    /// <see cref="ReviewModerationRow"/> conversion (<see cref="ToRow"/>)
    /// always runs after the query has executed, over the materialized list -
    /// LINQ-to-Objects at that point, not LINQ-to-Entities, so it can compute
    /// display fields freely.
    /// </remarks>
    private IQueryable<ReviewJoinRow> JoinNames(IQueryable<Review> reviews)
    {
        var query =
            from review in reviews
            join service in _context.Set<Service>() on review.ServiceId equals service.Id
            join category in _context.Set<Category>() on service.CategoryId equals category.Id
            join customer in _context.Set<Customer>() on review.CustomerId equals customer.Id
            select new ReviewJoinRow(review, service, category, customer);

        return query;
    }

    private static ReviewModerationRow ToRow(ReviewJoinRow joined) =>
        new(joined.Review, joined.Customer.Name, joined.Service.Name, joined.Category.Id, joined.Category.Name);

    /// <inheritdoc/>
    public async Task<ProviderVisibleReviewSearchResult> SearchVisibleForProviderAsync(
        Guid providerId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var filtered = _context.Reviews
            .Where(r => r.ProviderId == providerId && r.Status == ReviewStatus.Visible && !r.IsFlagged);

        int totalCount = await filtered.CountAsync(cancellationToken);

        var pagedReviews = filtered
            .OrderByDescending(r => r.CreatedAtUtc)
            .ApplyPaging(page, pageSize);

        // Same reasoning as JoinNames/SearchAsync above: join only over the
        // already-paged rows, then re-sort the small materialized list -
        // SQL joins do not guarantee preserving the driving query's order.
        var rows = await (
            from review in pagedReviews
            join customer in _context.Set<Customer>() on review.CustomerId equals customer.Id
            select new ProviderVisibleReviewRow(review, customer.Name)).ToListAsync(cancellationToken);

        rows = rows.OrderByDescending(r => r.Review.CreatedAtUtc).ToList();

        return new ProviderVisibleReviewSearchResult(rows, totalCount);
    }

    /// <summary>The raw entities behind one moderation row, before the display-only name projection in <see cref="ToRow"/>.</summary>
    private sealed record ReviewJoinRow(Review Review, Service Service, Category Category, Customer Customer);
}
