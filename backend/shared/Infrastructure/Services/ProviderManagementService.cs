using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Reviews;
using Nestly.Application.Serviceability;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderManagementService"/>
public class ProviderManagementService : IProviderManagementService
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderKycDocumentRepository _kycDocumentRepository;
    private readonly IProviderBackgroundCheckRepository _backgroundCheckRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IBookingProviderAssignmentRepository _assignmentRepository;
    private readonly IProviderEarningLedgerRepository _earningLedgerRepository;
    private readonly IProviderCapacityRepository _capacityRepository;
    private readonly IProviderServiceAreaRepository _serviceAreaRepository;
    private readonly IProviderSessionRepository _sessionRepository;
    private readonly IServiceabilityMappingManagementService _serviceabilityMappingManagementService;
    private readonly IProviderAvailabilityWindowRepository _availabilityWindowRepository;
    private readonly IReviewRepository _reviewRepository;

    public ProviderManagementService(
        IProviderRepository providerRepository,
        IProviderKycDocumentRepository kycDocumentRepository,
        IProviderBackgroundCheckRepository backgroundCheckRepository,
        IBookingRepository bookingRepository,
        IBookingProviderAssignmentRepository assignmentRepository,
        IProviderEarningLedgerRepository earningLedgerRepository,
        IProviderCapacityRepository capacityRepository,
        IProviderServiceAreaRepository serviceAreaRepository,
        IProviderSessionRepository sessionRepository,
        IServiceabilityMappingManagementService serviceabilityMappingManagementService,
        IProviderAvailabilityWindowRepository availabilityWindowRepository,
        IReviewRepository reviewRepository)
    {
        _providerRepository = providerRepository;
        _kycDocumentRepository = kycDocumentRepository;
        _backgroundCheckRepository = backgroundCheckRepository;
        _bookingRepository = bookingRepository;
        _assignmentRepository = assignmentRepository;
        _earningLedgerRepository = earningLedgerRepository;
        _capacityRepository = capacityRepository;
        _serviceAreaRepository = serviceAreaRepository;
        _sessionRepository = sessionRepository;
        _serviceabilityMappingManagementService = serviceabilityMappingManagementService;
        _availabilityWindowRepository = availabilityWindowRepository;
        _reviewRepository = reviewRepository;
    }

    public async Task<Result<ProviderSearchResponse>> SearchAsync(ProviderSearchRequest request)
    {
        var filter = new ProviderSearchFilter(
            request.Name, request.Phone, request.Status, request.OnboardingStatus, request.CityId, request.Page, request.PageSize);
        var result = await _providerRepository.SearchAsync(filter);

        // Batched per page rather than per row (task 371) - one query for
        // however many providers this page holds, not N.
        var serviceCitiesByProvider = await _serviceAreaRepository.ListActiveCityNamesByProviderAsync(
            result.Rows.Select(p => p.Id).ToList());

        var items = result.Rows.Select(p => new ProviderSummaryResponse(
            p.Id, p.LegalName, p.DisplayName, p.Phone, p.Email, p.Status, p.OnboardingStatus, p.CreatedAt,
            serviceCitiesByProvider.GetValueOrDefault(p.Id, []))).ToList();

        return new ProviderSearchResponse(items, result.TotalCount, request.Page, request.PageSize);
    }

    public async Task<Result<ProviderDetailResponse>> GetDetailAsync(Guid providerId)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Error.NotFound("Provider.NotFound", "Provider was not found.");
        }

        return await BuildDetailAsync(provider);
    }

    public async Task<Result<ProviderDetailResponse>> CreateAsync(CreateProviderRequest request)
    {
        if (await _providerRepository.ExistsByPhoneAsync(request.Phone))
        {
            return Error.Conflict("Provider.PhoneAlreadyExists", "A provider with this phone number already exists.");
        }

        Provider provider;
        try
        {
            provider = new Provider(Guid.NewGuid(), request.LegalName, request.DisplayName, ProviderType.Individual, request.Phone, request.Email);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("Provider.InvalidProfile", ex.Message);
        }

        await _providerRepository.AddAsync(provider);

        // Row 31, docs/OPEN-FIXES-FEATURES.csv: without this the provider is
        // invisible to matching until they set availability by hand.
        await _availabilityWindowRepository.ReplaceForProviderAsync(
            provider.Id, ProviderAvailabilityWindow.DefaultWeeklySchedule(provider.Id));

        return await BuildDetailAsync(provider);
    }

    public async Task<Result<ProviderDetailResponse>> UpdateAsync(Guid providerId, UpdateProviderRequest request)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Error.NotFound("Provider.NotFound", "Provider was not found.");
        }

        try
        {
            provider.UpdateProfile(request.LegalName, request.DisplayName, request.Email);
            provider.UpdateLocation(request.Latitude, request.Longitude);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("Provider.InvalidProfile", ex.Message);
        }

        await _providerRepository.UpdateAsync(provider);

        return await BuildDetailAsync(provider);
    }

    public async Task<Result<ProviderDetailResponse>> SuspendAsync(Guid providerId, SuspendProviderRequest request)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Error.NotFound("Provider.NotFound", "Provider was not found.");
        }

        if (provider.Status == ProviderStatus.Suspended)
        {
            return Error.Business("Provider.AlreadySuspended", "This provider is already suspended.");
        }

        provider.ChangeStatus(ProviderStatus.Suspended);
        await _providerRepository.UpdateAsync(provider);

        // Bug 3 auto-disable: mirror of ReactivateAsync's auto-enable below -
        // a suspended provider's coverage no longer counts. Their skill/area
        // rows are untouched by this status change, so no "before" snapshot
        // is needed - AutoDisableUnservedMappingsAsync reads current
        // coverage itself.
        await _serviceabilityMappingManagementService.AutoDisableUnservedMappingsAsync(providerId);

        return await BuildDetailAsync(provider);
    }

    public async Task<Result<ProviderDetailResponse>> ReactivateAsync(Guid providerId)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Error.NotFound("Provider.NotFound", "Provider was not found.");
        }

        if (provider.Status != ProviderStatus.Suspended)
        {
            return Error.Business("Provider.NotSuspended", "Only a suspended provider can be reactivated.");
        }

        provider.ChangeStatus(ProviderStatus.Active);
        await _providerRepository.UpdateAsync(provider);

        // Bug 3 auto-enable: a reactivated provider's existing skills/areas
        // become live coverage again - see ProviderKycApprovalService.ActivateAsync
        // for the same reasoning on first activation.
        await _serviceabilityMappingManagementService.AutoEnableProviderCoverageAsync(providerId);

        return await BuildDetailAsync(provider);
    }

    public async Task<Result<ProviderDetailResponse>> DeleteAsync(Guid providerId)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Error.NotFound("Provider.NotFound", "Provider was not found.");
        }

        if (provider.Status == ProviderStatus.Deactivated)
        {
            return Error.Business("Provider.AlreadyDeleted", "This provider's account has already been deleted.");
        }

        provider.SoftDelete();
        await _providerRepository.UpdateAsync(provider);
        await _sessionRepository.RevokeAllForProviderAsync(providerId);

        // Bug 3 auto-disable: same as SuspendAsync - a deleted provider's
        // coverage no longer counts.
        await _serviceabilityMappingManagementService.AutoDisableUnservedMappingsAsync(providerId);

        return await BuildDetailAsync(provider);
    }

    public async Task<Result<ProviderPerformanceResponse>> GetPerformanceAsync(Guid providerId)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Error.NotFound("Provider.NotFound", "Provider was not found.");
        }

        var assignments = await AssignmentsForProviderAsync(providerId);
        var bookings = await _bookingRepository.ListByAssignedProviderAsync(providerId);
        var latestLedgerEntry = await _earningLedgerRepository.GetLatestAsync(providerId);
        var rating = await _reviewRepository.GetProviderRatingAsync(providerId);
        var stats = SummarizeAssignments(assignments);

        return new ProviderPerformanceResponse(
            providerId,
            TotalAssignments: assignments.Count,
            AcceptedAssignments: assignments.Count(a => a.Status == BookingProviderAssignmentStatus.Accepted),
            RejectedAssignments: assignments.Count(a => a.Status == BookingProviderAssignmentStatus.Rejected),
            CompletedJobs: bookings.Count(b => b.Status == BookingStatus.Completed),
            InProgressJobs: bookings.Count(b => b.Status == BookingStatus.InProgress),
            LifetimeEarnings: latestLedgerEntry?.BalanceAfter ?? 0m,
            AcceptanceRatePercent: stats.AcceptanceRatePercent,
            AverageResponseTimeMinutes: stats.AverageResponseTimeMinutes,
            CompletionRatePercent: stats.CompletionRatePercent,
            AverageRating: rating?.AverageRating,
            RatingCount: rating?.ReviewCount ?? 0);
    }

    /// <inheritdoc/>
    public async Task<Result<ProviderPerformanceListResponse>> ListPerformanceAsync(ProviderPerformanceListRequest request)
    {
        var providers = await _providerRepository.ListAllAsync();

        var sinceUtc = DateTime.UtcNow.AddDays(-request.PeriodDays);
        var assignmentsSince = await _assignmentRepository.ListSinceAsync(sinceUtc);
        var assignmentsByProvider = assignmentsSince
            .GroupBy(a => a.ProviderId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<BookingProviderAssignment>)g.ToList());

        var ratingsByProvider = await _reviewRepository.GetProviderRatingsAsync();

        var rows = providers.Select(p =>
        {
            var assignments = assignmentsByProvider.GetValueOrDefault(p.Id, Array.Empty<BookingProviderAssignment>());
            var stats = SummarizeAssignments(assignments);
            var rating = ratingsByProvider.GetValueOrDefault(p.Id);

            return new ProviderPerformanceSummaryResponse(
                p.Id,
                p.DisplayName,
                p.Status,
                OffersReceived: assignments.Count,
                AcceptedOffers: stats.AcceptedOffers,
                AcceptanceRatePercent: stats.AcceptanceRatePercent,
                AverageResponseTimeMinutes: stats.AverageResponseTimeMinutes,
                CompletedJobs: stats.CompletedOffers,
                CompletionRatePercent: stats.CompletionRatePercent,
                AverageRating: rating?.AverageRating,
                RatingCount: rating?.ReviewCount ?? 0);
        }).ToList();

        var ordered = OrderPerformanceRows(rows, request.SortBy, request.SortDescending);

        (int page, int pageSize) = PagedQueryExtensions.Normalize(request.Page, request.PageSize);
        var pageItems = ordered.Skip(PagedQueryExtensions.Offset(page, pageSize)).Take(pageSize).ToList();

        return new ProviderPerformanceListResponse(pageItems, rows.Count, page, pageSize, request.PeriodDays);
    }

    /// <summary>
    /// Ranks the whole in-memory row set by a computed column - see
    /// <see cref="IProviderRepository.ListAllAsync"/>'s doc comment for why
    /// this happens after loading every provider rather than as a DB-level
    /// ORDER BY. Nulls (a provider with no data for that column yet) always
    /// sort last regardless of direction - "no data" is not the same as
    /// "worst", and burying it at the bottom either way keeps it out of both
    /// the "best" and "worst" ends the admin is actually looking for.
    /// </summary>
    private static IReadOnlyList<ProviderPerformanceSummaryResponse> OrderPerformanceRows(
        List<ProviderPerformanceSummaryResponse> rows, ProviderPerformanceSortField sortBy, bool descending) =>
        sortBy switch
        {
            ProviderPerformanceSortField.DisplayName => descending
                ? rows.OrderByDescending(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()
                : rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            ProviderPerformanceSortField.OffersReceived => descending
                ? rows.OrderByDescending(r => r.OffersReceived).ToList()
                : rows.OrderBy(r => r.OffersReceived).ToList(),
            ProviderPerformanceSortField.AcceptanceRate => OrderByNullable(rows, r => r.AcceptanceRatePercent, descending),
            ProviderPerformanceSortField.AverageResponseTime => OrderByNullable(rows, r => r.AverageResponseTimeMinutes, descending),
            ProviderPerformanceSortField.CompletionRate => OrderByNullable(rows, r => r.CompletionRatePercent, descending),
            ProviderPerformanceSortField.AverageRating => OrderByNullable(rows, r => r.AverageRating, descending),
            _ => rows
        };

    private static IReadOnlyList<ProviderPerformanceSummaryResponse> OrderByNullable(
        List<ProviderPerformanceSummaryResponse> rows, Func<ProviderPerformanceSummaryResponse, double?> selector, bool descending)
    {
        var withValueFirst = rows.OrderBy(r => selector(r).HasValue ? 0 : 1);
        return (descending ? withValueFirst.ThenByDescending(selector) : withValueFirst.ThenBy(selector)).ToList();
    }

    /// <summary>
    /// The rate/response-time math shared by <see cref="GetPerformanceAsync"/>
    /// (one provider, all-time) and <see cref="ListPerformanceAsync"/> (every
    /// provider, windowed) - one place computing "accepted", "completed",
    /// acceptance rate, average response time and completion rate from a set
    /// of <see cref="BookingProviderAssignment"/> rows so the two endpoints
    /// can never drift apart on what these numbers mean.
    /// </summary>
    private static AssignmentStats SummarizeAssignments(IReadOnlyCollection<BookingProviderAssignment> assignments)
    {
        int total = assignments.Count;

        // "Accepted" for rate purposes means the provider said yes at some
        // point - a job that has since finished (Completed) was accepted
        // first (BookingProviderAssignment.Complete only runs from Accepted),
        // so excluding it would make AcceptanceRatePercent silently drop
        // every provider's oldest, most successful jobs.
        int accepted = assignments.Count(a =>
            a.Status is BookingProviderAssignmentStatus.Accepted or BookingProviderAssignmentStatus.Completed);
        int completed = assignments.Count(a => a.Status == BookingProviderAssignmentStatus.Completed);

        double? acceptanceRate = total > 0 ? Math.Round(100.0 * accepted / total, 1) : null;
        double? completionRate = accepted > 0 ? Math.Round(100.0 * completed / accepted, 1) : null;

        // Only rows the provider actually answered carry a real response
        // time - Expired (never answered) and Withdrawn/Reassigned (the
        // booking moved on, not the provider's own response) are excluded so
        // they cannot masquerade as a fast or slow reply that never happened.
        var responded = assignments
            .Where(a => a.RespondedAt.HasValue && a.Status is
                BookingProviderAssignmentStatus.Accepted or
                BookingProviderAssignmentStatus.Rejected or
                BookingProviderAssignmentStatus.Completed)
            .ToList();
        double? averageResponseMinutes = responded.Count > 0
            ? Math.Round(responded.Average(a => (a.RespondedAt!.Value - a.AssignedAt).TotalMinutes), 1)
            : null;

        return new AssignmentStats(accepted, completed, acceptanceRate, averageResponseMinutes, completionRate);
    }

    private readonly record struct AssignmentStats(
        int AcceptedOffers,
        int CompletedOffers,
        double? AcceptanceRatePercent,
        double? AverageResponseTimeMinutes,
        double? CompletionRatePercent);

    public async Task<Result<ProviderCapacityResponse>> GetCapacityAsync(Guid providerId)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Error.NotFound("Provider.NotFound", "Provider was not found.");
        }

        var capacity = await _capacityRepository.GetByProviderAsync(providerId);
        return new ProviderCapacityResponse(providerId, capacity?.MaxJobsPerDay, capacity?.MaxJobsPerSlot);
    }

    public async Task<Result<ProviderCapacityResponse>> SetCapacityAsync(Guid providerId, SetProviderCapacityRequest request)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Error.NotFound("Provider.NotFound", "Provider was not found.");
        }

        ProviderCapacity capacity;
        try
        {
            capacity = new ProviderCapacity(Guid.NewGuid(), providerId, request.MaxJobsPerDay, request.MaxJobsPerSlot);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Error.Validation("ProviderCapacity.InvalidLimits", ex.Message);
        }

        await _capacityRepository.UpsertAsync(capacity);

        return new ProviderCapacityResponse(providerId, capacity.MaxJobsPerDay, capacity.MaxJobsPerSlot);
    }

    /// <summary>
    /// Every assignment ever made to this provider, across every booking
    /// (tasks 258, 263).
    ///
    /// This used to start from <c>IBookingRepository.ListByAssignedProviderAsync</c>
    /// and pull each of those bookings' assignment history. That filters on
    /// <c>Booking.AssignedProviderId</c> - the live assignment only - so a
    /// booking this provider rejected, which is then reassigned to someone
    /// else, no longer pointed here and its Rejected row was never counted.
    /// Rejection is exactly the case that gets reassigned away, so
    /// RejectedAssignments reported near-zero however often the provider
    /// actually declined. It was also one query per booking.
    ///
    /// <c>ListByProviderAsync</c> answers the question directly, in one
    /// query, and keeps the rejected/superseded rows the metric is about.
    /// </summary>
    private Task<IReadOnlyList<BookingProviderAssignment>> AssignmentsForProviderAsync(Guid providerId) =>
        _assignmentRepository.ListByProviderAsync(providerId);

    private async Task<ProviderDetailResponse> BuildDetailAsync(Provider provider)
    {
        var documents = await _kycDocumentRepository.GetByProviderAsync(provider.Id);
        var backgroundChecks = await _backgroundCheckRepository.ListByProviderAsync(provider.Id);

        return ProviderDetailMapper.ToDetailResponse(provider, documents, backgroundChecks);
    }
}
