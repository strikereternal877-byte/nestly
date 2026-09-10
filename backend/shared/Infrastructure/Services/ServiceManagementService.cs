using System.Text.Json;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Abstractions.Caching;
using Nestly.Application.Bookings;
using Nestly.Application.Catalog;
using Nestly.Application.Serviceability;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>Admin CRUD over services/packages (SRS 12.6, task 105).</summary>
public class ServiceManagementService : IServiceManagementService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceRepository _serviceRepository;
    private readonly ICategoryRepository _categoryRepository;
    private readonly IServiceGroupRepository _serviceGroupRepository;
    private readonly IServiceMediaRepository _serviceMediaRepository;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly ICacheService _cache;
    private readonly IServiceCityPriceRepository _serviceCityPriceRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IServiceabilityMappingManagementService _mappingManagementService;

    public ServiceManagementService(
        IServiceRepository serviceRepository,
        ICategoryRepository categoryRepository,
        IServiceGroupRepository serviceGroupRepository,
        IServiceMediaRepository serviceMediaRepository,
        IAuditLogWriter auditLogWriter,
        ICacheService cache,
        IServiceCityPriceRepository serviceCityPriceRepository,
        IBookingRepository bookingRepository,
        IServiceabilityMappingManagementService mappingManagementService)
    {
        _serviceRepository = serviceRepository;
        _categoryRepository = categoryRepository;
        _serviceGroupRepository = serviceGroupRepository;
        _serviceMediaRepository = serviceMediaRepository;
        _auditLogWriter = auditLogWriter;
        _cache = cache;
        _serviceCityPriceRepository = serviceCityPriceRepository;
        _bookingRepository = bookingRepository;
        _mappingManagementService = mappingManagementService;
    }

    public async Task<IReadOnlyList<ServiceAdminResponse>> ListAsync(Guid? categoryId)
    {
        var services = await _serviceRepository.ListAllAsync(categoryId);
        var categoryNames = await BuildCategoryNameLookupAsync(services.Select(s => s.CategoryId));
        return services.Select(s => ToResponse(s, categoryNames)).ToList();
    }

    public async Task<Result<ServiceAdminResponse>> GetByIdAsync(Guid id)
    {
        var service = await _serviceRepository.GetByIdAsync(id);
        if (service is null)
        {
            return NotFound();
        }

        var category = await _categoryRepository.GetByIdAsync(service.CategoryId);
        return ToResponse(service, category?.Name ?? string.Empty);
    }

    public async Task<Result<ServiceAdminResponse>> CreateAsync(ServiceCreateRequest request)
    {
        var category = await _categoryRepository.GetByIdAsync(request.CategoryId);
        if (category is null)
        {
            return Error.NotFound("Category.NotFound", "The specified category does not exist.");
        }

        if (await _serviceRepository.ExistsBySlugAsync(request.Slug))
        {
            return Error.Conflict("Service.DuplicateSlug", "A service with this slug already exists.");
        }

        var groupValidation = await ValidateServiceGroupAsync(request.ServiceGroupId, request.CategoryId);
        if (groupValidation.IsFailure)
        {
            return Result.Failure<ServiceAdminResponse>(groupValidation.Error);
        }

        var service = new Service(Guid.NewGuid(), request.CategoryId, request.Name, request.Slug, request.Description, request.Price);
        ApplyEditableFields(service, request.ShortDescription, request.Inclusions, request.Exclusions,
            request.CancellationPolicy, request.ReschedulePolicy, request.DurationMinutes, request.SortOrder,
            request.SeoTitle, request.SeoMetaDescription, request.PricingType, request.IsTaxApplicable,
            request.IsAddOnAllowed, request.IsQuantityAllowed, request.IsInspectionBased, request.IsSlotRequired,
            request.IsAddressRequired, request.IsCustomerNoteAllowed, request.IsDurationBased, request.CoverImageUrl, request.ServiceGroupId);

        // Staged before AddAsync so its own SaveChangesAsync commits the
        // audit row in the same transaction as the new service.
        await _auditLogWriter.WriteAsync(new AuditEntry(
            "Service", service.Id.ToString(), "Created", OldValues: null, NewValues: Serialize(service, category.Name)));

        await _serviceRepository.AddAsync(service);

        // Explicit (not domain-event-driven, unlike Activate/Deactivate/
        // PriceChange): a newly created service can already belong to a
        // group, and the category-detail cache's section-header rendering
        // depends on that being fresh.
        await _cache.RemoveAsync(CacheKeys.Category(request.CategoryId));

        return ToResponse(service, category.Name);
    }

    public async Task<Result<ServiceAdminResponse>> UpdateAsync(Guid id, ServiceUpdateRequest request)
    {
        var service = await _serviceRepository.GetByIdAsync(id);
        if (service is null)
        {
            return NotFound();
        }

        var category = await _categoryRepository.GetByIdAsync(request.CategoryId);
        if (category is null)
        {
            return Error.NotFound("Category.NotFound", "The specified category does not exist.");
        }

        if (await _serviceRepository.ExistsBySlugAsync(request.Slug, excludeId: id))
        {
            return Error.Conflict("Service.DuplicateSlug", "A service with this slug already exists.");
        }

        var groupValidation = await ValidateServiceGroupAsync(request.ServiceGroupId, request.CategoryId);
        if (groupValidation.IsFailure)
        {
            return Result.Failure<ServiceAdminResponse>(groupValidation.Error);
        }

        var oldCategory = await _categoryRepository.GetByIdAsync(service.CategoryId);
        string oldValues = Serialize(service, oldCategory?.Name ?? string.Empty);
        Guid oldCategoryId = service.CategoryId;

        service.SetCategoryId(request.CategoryId);
        service.SetName(request.Name);
        service.SetSlug(request.Slug);
        service.SetDescription(request.Description);
        service.SetPrice(request.Price);
        ApplyEditableFields(service, request.ShortDescription, request.Inclusions, request.Exclusions,
            request.CancellationPolicy, request.ReschedulePolicy, request.DurationMinutes, request.SortOrder,
            request.SeoTitle, request.SeoMetaDescription, request.PricingType, request.IsTaxApplicable,
            request.IsAddOnAllowed, request.IsQuantityAllowed, request.IsInspectionBased, request.IsSlotRequired,
            request.IsAddressRequired, request.IsCustomerNoteAllowed, request.IsDurationBased, request.CoverImageUrl, request.ServiceGroupId);
        service.MarkUpdated(oldCategoryId);

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "Service", service.Id.ToString(), "Updated", oldValues, Serialize(service, category.Name)));

        await _serviceRepository.UpdateAsync(service);

        // Explicit (see CreateAsync): the section-header grouping this
        // service renders under lives in the category-detail cache, which no
        // domain event evicts on a plain field edit.
        await _cache.RemoveAsync(CacheKeys.Category(oldCategoryId));
        if (oldCategoryId != request.CategoryId)
        {
            await _cache.RemoveAsync(CacheKeys.Category(request.CategoryId));
        }

        return ToResponse(service, category.Name);
    }

    /// <summary>A service's group, if any, must belong to the same category the service itself is being saved under.</summary>
    private async Task<Result> ValidateServiceGroupAsync(Guid? serviceGroupId, Guid categoryId)
    {
        if (serviceGroupId is null)
        {
            return Result.Success();
        }

        var group = await _serviceGroupRepository.GetByIdAsync(serviceGroupId.Value);
        if (group is null)
        {
            return Result.Failure(Error.NotFound("ServiceGroup.NotFound", "The specified service group does not exist."));
        }

        if (group.CategoryId != categoryId)
        {
            return Result.Failure(Error.Validation(
                "ServiceGroup.CategoryMismatch", "The specified service group belongs to a different category."));
        }

        return Result.Success();
    }

    public async Task<Result> SetActiveAsync(Guid id, bool isActive)
    {
        var service = await _serviceRepository.GetByIdAsync(id);
        if (service is null)
        {
            return Result.Failure(NotFound().Error);
        }

        if (isActive) service.Activate(); else service.Deactivate();

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "Service", service.Id.ToString(), isActive ? "Activated" : "Deactivated"));

        await _serviceRepository.UpdateAsync(service);
        return Result.Success();
    }

    public async Task<Result> SetFeaturedAsync(Guid id, bool isFeatured)
    {
        var service = await _serviceRepository.GetByIdAsync(id);
        if (service is null)
        {
            return Result.Failure(NotFound().Error);
        }

        if (isFeatured) service.Feature(); else service.Unfeature();

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "Service", service.Id.ToString(), isFeatured ? "Featured" : "Unfeatured"));

        await _serviceRepository.UpdateAsync(service);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<ServiceMediaResponse>>> ListMediaAsync(Guid serviceId)
    {
        if (await _serviceRepository.GetByIdAsync(serviceId) is null)
        {
            return NotFound().Error;
        }

        var media = await _serviceMediaRepository.ListByServiceAsync(serviceId);
        return media.Select(ToResponse).ToList();
    }

    public async Task<Result<ServiceMediaResponse>> AddMediaAsync(Guid serviceId, ServiceMediaCreateRequest request)
    {
        if (await _serviceRepository.GetByIdAsync(serviceId) is null)
        {
            return NotFound().Error;
        }

        var media = new ServiceMedia(Guid.NewGuid(), serviceId, request.Url);

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "Service", serviceId.ToString(), "GalleryImageAdded", OldValues: null, NewValues: request.Url));

        await _serviceMediaRepository.AddAsync(media);

        return ToResponse(media);
    }

    public async Task<Result> RemoveMediaAsync(Guid serviceId, Guid mediaId)
    {
        var media = await _serviceMediaRepository.GetByIdAsync(mediaId);
        if (media is null || media.ServiceId != serviceId)
        {
            return Result.Failure(Error.NotFound("ServiceMedia.NotFound", "The specified gallery image does not exist for this service."));
        }

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "Service", serviceId.ToString(), "GalleryImageRemoved", OldValues: media.Url, NewValues: null));

        await _serviceMediaRepository.DeleteAsync(media);
        return Result.Success();
    }

    private static void ApplyEditableFields(
        Service service,
        string? shortDescription,
        string inclusions,
        string exclusions,
        string? cancellationPolicy,
        string? reschedulePolicy,
        int durationMinutes,
        int sortOrder,
        string? seoTitle,
        string? seoMetaDescription,
        string pricingType,
        bool isTaxApplicable,
        bool isAddOnAllowed,
        bool isQuantityAllowed,
        bool isInspectionBased,
        bool isSlotRequired,
        bool isAddressRequired,
        bool isCustomerNoteAllowed,
        bool isDurationBased,
        string? coverImageUrl,
        Guid? serviceGroupId)
    {
        service.SetShortDescription(shortDescription);
        service.SetCoverImageUrl(coverImageUrl);
        service.SetServiceGroupId(serviceGroupId);
        service.SetInclusions(inclusions);
        service.SetExclusions(exclusions);
        service.SetCancellationPolicy(cancellationPolicy);
        service.SetReschedulePolicy(reschedulePolicy);
        service.SetDuration(durationMinutes);
        service.SetSortOrder(sortOrder);
        service.SetSeo(seoTitle, seoMetaDescription);
        service.SetPricingType(Enum.Parse<ServicePricingType>(pricingType));
        service.SetOptions(isTaxApplicable, isAddOnAllowed, isQuantityAllowed, isInspectionBased,
            isSlotRequired, isAddressRequired, isCustomerNoteAllowed, isDurationBased);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CatalogHealthIssueResponse>> ListHealthIssuesAsync()
    {
        // SearchActiveAsync(string.Empty) is the same "every active service"
        // idiom ServiceabilityMappingManagementService.ListServicesAsync
        // already uses - see that repository method's own doc comment.
        var activeServices = await _serviceRepository.SearchActiveAsync(string.Empty);
        if (activeServices.Count == 0)
        {
            return [];
        }

        // Four independent lookups, each one query, rather than a per-service
        // round trip for any of them (avoids N+1 over the active catalog).
        var categoryNamesTask = BuildCategoryNameLookupAsync(activeServices.Select(s => s.CategoryId));
        var pricedServiceIdsTask = _serviceCityPriceRepository.ListServiceIdsWithActivePriceAsync();
        var everBookedServiceIdsTask = _bookingRepository.ListServiceIdsEverBookedAsync();
        var unmappedServiceIdsTask = _mappingManagementService.ListUnmappedActiveServicesAsync();
        await Task.WhenAll(categoryNamesTask, pricedServiceIdsTask, everBookedServiceIdsTask, unmappedServiceIdsTask);

        var categoryNames = categoryNamesTask.Result;
        var pricedServiceIds = pricedServiceIdsTask.Result.ToHashSet();
        var everBookedServiceIds = everBookedServiceIdsTask.Result.ToHashSet();
        var unmappedServiceIds = unmappedServiceIdsTask.Result.Select(u => u.ServiceId).ToHashSet();

        var issues = new List<CatalogHealthIssueResponse>();
        foreach (var service in activeServices)
        {
            var reasons = new List<string>();
            if (!pricedServiceIds.Contains(service.Id))
            {
                reasons.Add(CatalogHealthReason.NoPrice);
            }

            if (string.IsNullOrWhiteSpace(service.CoverImageUrl))
            {
                reasons.Add(CatalogHealthReason.NoImage);
            }

            if (unmappedServiceIds.Contains(service.Id))
            {
                reasons.Add(CatalogHealthReason.NoMapping);
            }

            if (!everBookedServiceIds.Contains(service.Id))
            {
                reasons.Add(CatalogHealthReason.NeverBooked);
            }

            if (reasons.Count == 0)
            {
                continue;
            }

            issues.Add(new CatalogHealthIssueResponse(
                service.Id,
                service.Name,
                service.Slug,
                service.CategoryId,
                categoryNames.GetValueOrDefault(service.CategoryId, string.Empty),
                reasons));
        }

        return issues.OrderBy(i => i.ServiceName).ToList();
    }

    // Task NESTLY-011: one batched lookup instead of a GetByIdAsync per
    // distinct category id (mirrors ReferralAdminService.SearchAsync's use of
    // the equivalent ICustomerRepository.GetNamesByIdsAsync).
    private async Task<IReadOnlyDictionary<Guid, string>> BuildCategoryNameLookupAsync(IEnumerable<Guid> categoryIds) =>
        await _categoryRepository.GetNamesByIdsAsync(categoryIds.Distinct().ToList());

    private static Result<ServiceAdminResponse> NotFound() =>
        Error.NotFound("Service.NotFound", "The specified service does not exist.");

    private static string Serialize(Service service, string categoryName) =>
        JsonSerializer.Serialize(ToResponse(service, categoryName), JsonOptions);

    private static ServiceAdminResponse ToResponse(Service service, IReadOnlyDictionary<Guid, string> categoryNames) =>
        ToResponse(service, categoryNames.GetValueOrDefault(service.CategoryId, string.Empty));

    private static ServiceAdminResponse ToResponse(Service service, string categoryName) => new(
        service.Id,
        service.CategoryId,
        categoryName,
        service.Name,
        service.Slug,
        service.Description,
        service.ShortDescription,
        service.Price,
        service.IsActive,
        service.Inclusions,
        service.Exclusions,
        service.CancellationPolicy,
        service.ReschedulePolicy,
        service.DurationMinutes,
        service.IsFeatured,
        service.SortOrder,
        service.SeoTitle,
        service.SeoMetaDescription,
        service.PricingType.ToString(),
        service.IsTaxApplicable,
        service.IsAddOnAllowed,
        service.IsQuantityAllowed,
        service.IsInspectionBased,
        service.IsSlotRequired,
        service.IsAddressRequired,
        service.IsCustomerNoteAllowed,
        service.IsDurationBased,
        service.CoverImageUrl,
        service.ServiceGroupId);

    private static ServiceMediaResponse ToResponse(ServiceMedia media) => new(media.Id, media.ServiceId, media.Url);
}
