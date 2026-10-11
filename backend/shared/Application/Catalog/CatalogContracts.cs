using Nestly.Application.Meals;
using Nestly.Domain;

namespace Nestly.Application.Catalog;

/// <summary>Category card for a listing page (SRS 11.1/11.5), scoped to a serviceable city.</summary>
public record CategorySummaryResponse(
    Guid Id,
    string Name,
    string Slug,
    string? IconUrl,
    string? BannerUrl,
    string? PageBannerUrl,
    bool IsFeatured);

public record ServiceAddOnSummaryResponse(
    Guid Id,
    string Name,
    string? Description,
    decimal Price);

/// <summary>
/// A service as it appears nested under a category detail (SRS 11.5).
/// <see cref="AddOns"/> is ungrouped add-ons only (Phase 3 catalog redesign) -
/// grouped add-ons aren't surfaced at this summary level, only on the service
/// detail page. <see cref="CoverImageUrl"/> is null until an admin sets one -
/// the customer app renders a graphic fallback rather than a broken image.
/// </summary>
public record ServiceSummaryResponse(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    decimal Price,
    IReadOnlyList<ServiceAddOnSummaryResponse> AddOns,
    string? CoverImageUrl,
    int DurationMinutes);

/// <summary>A priced/timed option a service can be booked as, as shown to a customer (Phase 3 catalog redesign).</summary>
public record ServiceVariantSummaryResponse(
    Guid Id,
    string Name,
    decimal Price,
    int DurationMinutes,
    string? InclusionsOverride);

/// <summary>
/// A named group of add-ons with a selection rule, as shown to a customer
/// (Phase 3 catalog redesign). <see cref="SelectionType"/> is the enum's
/// string name ("Single"/"Multiple"), not the C# enum type itself - no
/// <c>JsonStringEnumConverter</c> is registered app-wide, so an enum-typed
/// field here would serialize as its numeric ordinal instead (same reasoning
/// as <see cref="ServiceAdminResponse.PricingType"/>).
/// </summary>
public record ServiceAddOnGroupSummaryResponse(
    Guid Id,
    string Name,
    string SelectionType,
    int MinSelect,
    int? MaxSelect,
    IReadOnlyList<ServiceAddOnSummaryResponse> AddOns);

/// <summary>
/// A named section header for a subset of a category's services (e.g. "Repair
/// &amp; gas refill" under "AC"), as shown to a customer. Only ever included
/// when it has at least one active service - the UI must never render an
/// empty header. <see cref="Services"/> is ordered for display, same as
/// <see cref="CategoryDetailResponse.Services"/>.
/// </summary>
public record ServiceGroupSummaryResponse(
    Guid Id,
    string Name,
    IReadOnlyList<ServiceSummaryResponse> Services);

/// <summary>
/// A named section header for a subset of a category's subcategories (e.g.
/// "Large appliances" under "AC &amp; Appliance Repair"), as shown to a
/// customer. Only ever included when it has at least one active
/// subcategory - the UI must never render an empty header. Mirrors
/// <see cref="ServiceGroupSummaryResponse"/> exactly, one taxonomy level up.
/// </summary>
public record CategoryGroupSummaryResponse(
    Guid Id,
    string Name,
    IReadOnlyList<CategorySummaryResponse> Subcategories);

/// <summary>
/// Category detail: the category plus its active subcategories, services and
/// their add-ons (task 41, SRS 11.5; subcategories added Phase 3 catalog
/// redesign). <see cref="Services"/> is ungrouped services only (Appliance/
/// Service Group catalog redesign) - a service assigned to a group is
/// surfaced under its entry in <see cref="ServiceGroups"/> instead, never
/// both. For every category with no service groups (the default, and every
/// category before this field existed), <see cref="ServiceGroups"/> is empty
/// and <see cref="Services"/> behaves exactly as before. <see cref="Subcategories"/>/
/// <see cref="SubcategoryGroups"/> follow the identical ungrouped/grouped
/// split, one taxonomy level up.
/// </summary>
public record CategoryDetailResponse(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    string? IconUrl,
    string? BannerUrl,
    string? PageBannerUrl,
    IReadOnlyList<ServiceSummaryResponse> Services,
    IReadOnlyList<CategorySummaryResponse> Subcategories,
    IReadOnlyList<ServiceGroupSummaryResponse> ServiceGroups,
    IReadOnlyList<CategoryGroupSummaryResponse> SubcategoryGroups);

/// <summary>Service card for a "services within category" listing (task 42a, SRS 11.5.3).</summary>
public record ServiceListItemResponse(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    decimal Price,
    string? CoverImageUrl,
    int DurationMinutes);

/// <summary>One FAQ entry on a service detail page (task 52d, SRS 11.6.1).</summary>
public record ServiceFaqResponse(Guid Id, string Question, string Answer);

/// <summary>
/// Full service detail page content (task 42b, SRS 11.6.1). <see cref="AddOns"/>
/// is ungrouped add-ons only (Phase 3 catalog redesign) - grouped add-ons are
/// in <see cref="AddOnGroups"/> instead. <see cref="Variants"/> is empty for a
/// service with no priced/timed options, in which case <see cref="Price"/>
/// (unchanged) is the price to book at.
/// </summary>
public record ServiceDetailResponse(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    decimal Price,
    string Inclusions,
    string Exclusions,
    string? CancellationPolicy,
    string? ReschedulePolicy,
    Guid CategoryId,
    string CategoryName,
    string CategorySlug,
    IReadOnlyList<ServiceAddOnSummaryResponse> AddOns,
    IReadOnlyList<ServiceFaqResponse> Faqs,
    IReadOnlyList<ServiceVariantSummaryResponse> Variants,
    IReadOnlyList<ServiceAddOnGroupSummaryResponse> AddOnGroups,
    string? CoverImageUrl,
    int DurationMinutes,
    /// <summary>Whether this service is measured in units, so the storefront should offer a quantity stepper (SRS 12.6.3). False for flat-rate services, which book at a single fixed quantity - the customer UI hides the stepper and the pricing engine forces quantity to 1.</summary>
    bool IsQuantityAllowed,
    /// <summary>"Fixed", "Variable" or "MealBased". A MealBased service is booked by meals and persons, not quantity (docs/MEAL-SERVICE.md).</summary>
    string PricingType = "Fixed",
    /// <summary>Per-meal rates and default times of a MealBased service, so the storefront can show live prices as the customer picks; the server recomputes on booking.</summary>
    IReadOnlyList<MealOptionDto>? MealOptions = null);

/// <summary>One recent review shown in a service's rating summary (task 52f, SRS 11.6.1).</summary>
public record ServiceReviewItemResponse(Guid Id, int Rating, string? ReviewText, DateTime CreatedAtUtc);

/// <summary>
/// Rating summary for a service detail page (task 52f, SRS 11.6.1 "Reviews
/// and rating summary") - only visible (non-hidden) reviews, same as
/// <see cref="Nestly.Application.Reviews.IReviewRepository.ListByServiceAsync"/>.
/// </summary>
public record ServiceReviewSummaryResponse(
    double AverageRating,
    int TotalCount,
    IReadOnlyDictionary<int, int> RatingBreakdown,
    IReadOnlyList<ServiceReviewItemResponse> RecentReviews);

/// <summary>Combined category/service search results (task 42c, SRS 11.5-11.6, 24.3).</summary>
public record CatalogSearchResponse(
    IReadOnlyList<CategorySummaryResponse> Categories,
    IReadOnlyList<ServiceListItemResponse> Services);
