using Nestly.Application;
using Nestly.Application.Abstractions.Caching;
using Nestly.Application.Catalog;
using Nestly.Application.Meals;
using Nestly.Application.Reviews;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>Public service catalog queries (tasks 42a/42b/52d/52f), read-cached per task 49.</summary>
public class ServiceQueryService : IServiceQueryService
{
    private static readonly TimeSpan DetailTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(15);

    /// <summary>How many of a service's most recent reviews are shown alongside the rating summary (task 52f).</summary>
    private const int RecentReviewCount = 5;

    private readonly ICategoryRepository _categoryRepository;
    private readonly IServiceRepository _serviceRepository;
    private readonly IServiceAddOnRepository _addOnRepository;
    private readonly IServiceFaqRepository _faqRepository;
    private readonly IReviewRepository _reviewRepository;
    private readonly IServiceVariantRepository _variantRepository;
    private readonly IServiceAddOnGroupRepository _groupRepository;
    private readonly ICacheService _cache;

    public ServiceQueryService(
        ICategoryRepository categoryRepository,
        IServiceRepository serviceRepository,
        IServiceAddOnRepository addOnRepository,
        IServiceFaqRepository faqRepository,
        IReviewRepository reviewRepository,
        IServiceVariantRepository variantRepository,
        IServiceAddOnGroupRepository groupRepository,
        ICacheService cache)
    {
        _categoryRepository = categoryRepository;
        _serviceRepository = serviceRepository;
        _addOnRepository = addOnRepository;
        _faqRepository = faqRepository;
        _reviewRepository = reviewRepository;
        _variantRepository = variantRepository;
        _groupRepository = groupRepository;
        _cache = cache;
    }

    public async Task<Result<IReadOnlyList<ServiceListItemResponse>>> ListByCategoryAsync(Guid categoryId)
    {
        var category = await _categoryRepository.GetByIdAsync(categoryId);
        if (category is null || !await IsCategoryVisibleInHierarchyAsync(category))
        {
            return Error.NotFound("Catalog.CategoryNotFound", "The specified category does not exist.");
        }

        IReadOnlyList<ServiceListItemResponse> response = await _cache.GetOrCreateAsync(
            CacheKeys.ServicesByCategory(categoryId),
            async _ =>
            {
                var services = await _serviceRepository.ListActiveByCategoryAsync(categoryId);
                return (IReadOnlyList<ServiceListItemResponse>)services.Select(ToListItem).ToList();
            },
            ListTtl);

        return Result.Success(response);
    }

    public async Task<Result<ServiceDetailResponse>> GetDetailBySlugAsync(string slug)
    {
        // Slug -> id is a cheap unique-index hit and always fresh; only the
        // expensive category-breadcrumb + add-ons assembly below is cached.
        var service = await _serviceRepository.GetBySlugAsync(slug);
        if (service is null || !service.IsActive)
        {
            return Error.NotFound("Catalog.ServiceNotFound", "The specified service does not exist.");
        }

        var owningCategory = await _categoryRepository.GetByIdAsync(service.CategoryId);
        if (owningCategory is null || !await IsCategoryVisibleInHierarchyAsync(owningCategory))
        {
            return Error.NotFound("Catalog.ServiceNotFound", "The specified service does not exist.");
        }

        var detail = await _cache.GetOrCreateAsync(
            CacheKeys.Service(service.Id),
            async _ =>
            {
                var category = await _categoryRepository.GetByIdAsync(service.CategoryId);
                var addOns = await _addOnRepository.ListActiveByServiceAsync(service.Id);
                var faqs = await _faqRepository.ListByServiceAsync(service.Id);
                var variants = await _variantRepository.ListActiveByServiceIdsAsync([service.Id]);
                var groups = await _groupRepository.ListByServiceIdsAsync([service.Id]);

                // Phase 3 catalog redesign: split by GroupId rather than a
                // second query - one add-on list already fetched above.
                var ungroupedAddOns = addOns.Where(a => a.GroupId is null).ToList();
                var addOnsByGroup = addOns.Where(a => a.GroupId is not null)
                    .GroupBy(a => a.GroupId!.Value)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<ServiceAddOn>)g.ToList());

                var addOnGroupResponses = groups
                    .Where(g => addOnsByGroup.ContainsKey(g.Id))
                    .Select(g => new ServiceAddOnGroupSummaryResponse(
                        g.Id, g.Name, g.SelectionType.ToString(), g.MinSelect, g.MaxSelect,
                        addOnsByGroup[g.Id].Select(ToAddOnSummary).ToList()))
                    .ToList();

                return new ServiceDetailResponse(
                    service.Id,
                    service.Name,
                    service.Slug,
                    service.Description,
                    service.Price,
                    service.Inclusions,
                    service.Exclusions,
                    service.CancellationPolicy,
                    service.ReschedulePolicy,
                    service.CategoryId,
                    category?.Name ?? string.Empty,
                    category?.Slug ?? string.Empty,
                    ungroupedAddOns.Select(ToAddOnSummary).ToList(),
                    faqs.Select(ToFaqResponse).ToList(),
                    variants.Select(ToVariantSummary).ToList(),
                    addOnGroupResponses,
                    service.CoverImageUrl,
                    service.DurationMinutes,
                    service.IsQuantityAllowed,
                    service.PricingType.ToString(),
                    service.MealOptions.ToDtos());
            },
            DetailTtl);

        return detail;
    }

    public async Task<Result<ServiceReviewSummaryResponse>> GetReviewSummaryBySlugAsync(string slug)
    {
        var service = await _serviceRepository.GetBySlugAsync(slug);
        if (service is null || !service.IsActive)
        {
            return Error.NotFound("Catalog.ServiceNotFound", "The specified service does not exist.");
        }

        // Not cached (unlike the detail/list queries above) - a customer who
        // just submitted a review reasonably expects to see it reflected
        // immediately, and this is a single indexed query, not worth trading
        // that freshness away for.
        var reviews = await _reviewRepository.ListByServiceAsync(service.Id);

        var breakdown = Enumerable.Range(1, 5).ToDictionary(rating => rating, _ => 0);
        foreach (var review in reviews)
        {
            breakdown[review.Rating]++;
        }

        double averageRating = reviews.Count == 0 ? 0 : reviews.Average(r => r.Rating);

        return new ServiceReviewSummaryResponse(
            Math.Round(averageRating, 2),
            reviews.Count,
            breakdown,
            reviews.Take(RecentReviewCount).Select(ToReviewItem).ToList());
    }

    /// <summary>
    /// Whether <paramref name="category"/> and every one of its ancestor
    /// categories are active (SRS 11.5.4, 12.5.3) - mirrors
    /// <c>CategoryQueryService.IsVisibleInHierarchyAsync</c> so a service
    /// under a sub-category whose parent has been deactivated is hidden
    /// from customer-facing discovery even though the sub-category's and
    /// the service's own active flags are unchanged. Bounded to 32 hops as
    /// a defensive ceiling against malformed/cyclical parent data (a true
    /// cycle is already rejected at write time by
    /// <c>CategoryManagementService</c>).
    /// </summary>
    private async Task<bool> IsCategoryVisibleInHierarchyAsync(Category category)
    {
        Category? current = category;
        for (var hop = 0; hop < 32 && current is not null; hop++)
        {
            if (!current.IsActive)
            {
                return false;
            }

            if (current.ParentCategoryId is null)
            {
                return true;
            }

            current = await _categoryRepository.GetByIdAsync(current.ParentCategoryId.Value);
        }

        return false;
    }

    private static ServiceListItemResponse ToListItem(Service service) => new(
        service.Id,
        service.Name,
        service.Slug,
        service.Description,
        service.Price,
        service.CoverImageUrl,
        service.DurationMinutes);

    private static ServiceAddOnSummaryResponse ToAddOnSummary(ServiceAddOn addOn) => new(
        addOn.Id,
        addOn.Name,
        addOn.Description,
        addOn.Price);

    private static ServiceFaqResponse ToFaqResponse(ServiceFaq faq) => new(faq.Id, faq.Question, faq.Answer);

    private static ServiceVariantSummaryResponse ToVariantSummary(ServiceVariant variant) => new(
        variant.Id, variant.Name, variant.Price, variant.DurationMinutes, variant.InclusionsOverride);

    private static ServiceReviewItemResponse ToReviewItem(Review review) => new(
        review.Id, review.Rating, review.ReviewText, review.CreatedAtUtc);
}
