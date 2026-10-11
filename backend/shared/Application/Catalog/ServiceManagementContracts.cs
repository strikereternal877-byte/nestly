using Nestly.Application.Meals;

namespace Nestly.Application.Catalog;

/// <summary>
/// Admin view of a service (SRS 12.6.2/12.6.3), the full field set - distinct
/// from the consumer-facing <see cref="ServiceDetailResponse"/>, which omits
/// SEO/admin-only fields and is scoped to active services only.
/// </summary>
public sealed record ServiceAdminResponse(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    string Name,
    string Slug,
    string Description,
    string? ShortDescription,
    decimal Price,
    bool IsActive,
    string Inclusions,
    string Exclusions,
    string? CancellationPolicy,
    string? ReschedulePolicy,
    int DurationMinutes,
    bool IsFeatured,
    int SortOrder,
    string? SeoTitle,
    string? SeoMetaDescription,
    string PricingType,
    bool IsTaxApplicable,
    bool IsAddOnAllowed,
    bool IsQuantityAllowed,
    bool IsInspectionBased,
    bool IsSlotRequired,
    bool IsAddressRequired,
    bool IsCustomerNoteAllowed,
    bool IsDurationBased = false,
    string? CoverImageUrl = null,
    Guid? ServiceGroupId = null,
    /// <summary>Meal rates of a <c>MealBased</c> service (docs/MEAL-SERVICE.md); empty otherwise.</summary>
    IReadOnlyList<MealOptionDto>? MealOptions = null);

/// <summary>Admin create request for a service (SRS 12.6.1/12.6.2/12.6.3).</summary>
public sealed record ServiceCreateRequest(
    Guid CategoryId,
    string Name,
    string Slug,
    string Description,
    string? ShortDescription,
    decimal Price,
    string Inclusions,
    string Exclusions,
    string? CancellationPolicy,
    string? ReschedulePolicy,
    int DurationMinutes,
    int SortOrder,
    string? SeoTitle,
    string? SeoMetaDescription,
    string PricingType,
    bool IsTaxApplicable,
    bool IsAddOnAllowed,
    bool IsQuantityAllowed,
    bool IsInspectionBased,
    bool IsSlotRequired,
    bool IsAddressRequired,
    bool IsCustomerNoteAllowed,
    bool IsDurationBased = false,
    string? CoverImageUrl = null,
    Guid? ServiceGroupId = null);

/// <summary>
/// Admin update request for a service. Covers every editable field except
/// <see cref="Service.IsActive"/>/<see cref="Service.IsFeatured"/>, toggled
/// through their own dedicated endpoints so each change is audited as its
/// own distinct action (mirrors <see cref="CategoryUpdateRequest"/>).
/// </summary>
public sealed record ServiceUpdateRequest(
    Guid CategoryId,
    string Name,
    string Slug,
    string Description,
    string? ShortDescription,
    decimal Price,
    string Inclusions,
    string Exclusions,
    string? CancellationPolicy,
    string? ReschedulePolicy,
    int DurationMinutes,
    int SortOrder,
    string? SeoTitle,
    string? SeoMetaDescription,
    string PricingType,
    bool IsTaxApplicable,
    bool IsAddOnAllowed,
    bool IsQuantityAllowed,
    bool IsInspectionBased,
    bool IsSlotRequired,
    bool IsAddressRequired,
    bool IsCustomerNoteAllowed,
    bool IsDurationBased = false,
    string? CoverImageUrl = null,
    Guid? ServiceGroupId = null);

/// <summary>Gallery image attached to a service (SRS 12.6.2 "Gallery images").</summary>
public sealed record ServiceMediaResponse(Guid Id, Guid ServiceId, string Url);

/// <summary>Admin request to add one gallery image to a service.</summary>
public sealed record ServiceMediaCreateRequest(string Url);
