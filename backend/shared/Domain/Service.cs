using Nestly.BuildingBlocks.Primitives;
using Nestly.Domain.Events;
using Nestly.Domain.Meals;

namespace Nestly.Domain;

public class Service : AggregateRoot<Guid>
{
    public Guid CategoryId { get; private set; }

    /// <summary>
    /// Optional section header this service is displayed under within its
    /// category (e.g. "Super saver packages"). Null means the service
    /// renders directly under its category/appliance with no header
    /// (Model B) - the default, and how every service behaved before this
    /// field existed. Must belong to the same <see cref="CategoryId"/> as
    /// this service; enforced by <c>ServiceManagementService</c>, not here,
    /// since validating it requires a repository lookup this entity has no
    /// access to.
    /// </summary>
    public Guid? ServiceGroupId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>SEO-friendly identifier (SRS 12.6.2), globally unique like Category's.</summary>
    public string Slug { get; private set; } = string.Empty;

    /// <summary>Long-form description shown on the service detail page (SRS 12.6.2).</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Short summary shown on listing/search cards (SRS 12.6.2), distinct from the long <see cref="Description"/>.</summary>
    public string? ShortDescription { get; private set; }

    /// <summary>Photo shown on listing/search cards and the service detail page. Null renders a graphic fallback panel client-side rather than a broken image.</summary>
    public string? CoverImageUrl { get; private set; }

    public decimal Price { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>What the service covers (SRS 12.6.2), shown on the service detail page.</summary>
    public string Inclusions { get; private set; } = string.Empty;

    /// <summary>What the service explicitly does not cover (SRS 12.6.2).</summary>
    public string Exclusions { get; private set; } = string.Empty;

    /// <summary>Cancellation policy summary shown on the detail page (SRS 12.6.2, 11.6.1).</summary>
    public string? CancellationPolicy { get; private set; }

    /// <summary>Reschedule policy summary shown on the detail page (SRS 12.6.2, 11.6.1).</summary>
    public string? ReschedulePolicy { get; private set; }

    /// <summary>Estimated time to perform the service, in minutes (SRS 12.6.2).</summary>
    public int DurationMinutes { get; private set; }

    /// <summary>Whether this service is highlighted for promotion (SRS 12.6.2), mirrors <see cref="Category.IsFeatured"/>.</summary>
    public bool IsFeatured { get; private set; }

    /// <summary>Admin-controlled display order within its category (SRS 12.6.2).</summary>
    public int SortOrder { get; private set; }

    public string? SeoTitle { get; private set; }
    public string? SeoMetaDescription { get; private set; }

    /// <summary>Fixed package vs. variable/add-on pricing (SRS 12.6.3).</summary>
    public ServicePricingType PricingType { get; private set; }

    /// <summary>Whether tax is applied on top of <see cref="Price"/> (SRS 12.6.2).</summary>
    public bool IsTaxApplicable { get; private set; }

    /// <summary>Whether add-ons may be attached to a booking of this service (SRS 12.6.2).</summary>
    public bool IsAddOnAllowed { get; private set; }

    /// <summary>Whether a customer may book more than one unit of this service (SRS 12.6.2).</summary>
    public bool IsQuantityAllowed { get; private set; }

    /// <summary>Whether the service requires an inspection visit before it can be scheduled (SRS 12.6.3).</summary>
    public bool IsInspectionBased { get; private set; }

    /// <summary>Whether booking this service requires picking a slot (SRS 12.6.3).</summary>
    public bool IsSlotRequired { get; private set; }

    /// <summary>Whether booking this service requires a customer address (SRS 12.6.3).</summary>
    public bool IsAddressRequired { get; private set; }

    /// <summary>Whether the customer may attach a free-text note when booking (SRS 12.6.3).</summary>
    public bool IsCustomerNoteAllowed { get; private set; }

    /// <summary>
    /// Whether this service is sold as a block of <em>time</em> the provider
    /// owes the customer (e.g. "2 hours of cleaning"), as opposed to a
    /// fixed-scope job that is done when the work is done (e.g. an AC repair).
    /// It governs early release: for a duration-based service the provider stays
    /// committed for the whole booked window even if the checklist finishes
    /// early, whereas a fixed-scope service frees the provider on verified
    /// completion. Snapshotted onto the booking at creation so a later change
    /// here never alters an existing booking's commitment.
    /// </summary>
    public bool IsDurationBased { get; private set; }

    private List<MealOption> _mealOptions = [];

    /// <summary>Per-meal rates and cooking times; set only for <see cref="ServicePricingType.MealBased"/> services (docs/MEAL-SERVICE.md).</summary>
    public IReadOnlyList<MealOption> MealOptions
    {
        get => _mealOptions;
        private set => _mealOptions = value.ToList();
    }

    protected Service() { }

    public Service(Guid id, Guid categoryId, string name, string slug, string description, decimal price) : base(id)
    {
        CategoryId = categoryId;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Slug = slug ?? throw new ArgumentNullException(nameof(slug));
        Description = description ?? string.Empty;
        Price = price > 0 ? price : throw new ArgumentOutOfRangeException(nameof(price));
        IsActive = true;
        DurationMinutes = 60;
        IsFeatured = false;
        SortOrder = 0;
        PricingType = ServicePricingType.Fixed;
        IsTaxApplicable = true;
        IsAddOnAllowed = true;
        IsQuantityAllowed = false;
        IsInspectionBased = false;
        IsSlotRequired = true;
        IsAddressRequired = true;
        IsCustomerNoteAllowed = true;
        IsDurationBased = false;
        RaiseDomainEvent(new ServiceCreatedEvent(Id, CategoryId));
    }

    public void SetName(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));
    public void SetSlug(string slug) => Slug = slug ?? throw new ArgumentNullException(nameof(slug));
    public void SetDescription(string d) => Description = d ?? string.Empty;
    public void SetShortDescription(string? shortDescription) => ShortDescription = shortDescription;
    public void SetCoverImageUrl(string? coverImageUrl) => CoverImageUrl = coverImageUrl;
    public void SetCategoryId(Guid categoryId) => CategoryId = categoryId;
    public void SetServiceGroupId(Guid? serviceGroupId) => ServiceGroupId = serviceGroupId;

    public void SetPrice(decimal price)
    {
        decimal validated = price > 0 ? price : throw new ArgumentOutOfRangeException(nameof(price));
        if (validated == Price) return;
        decimal oldPrice = Price;
        Price = validated;
        RaiseDomainEvent(new ServicePriceChangedEvent(Id, oldPrice, Price));
    }

    public void SetInclusions(string inclusions) => Inclusions = inclusions ?? string.Empty;
    public void SetExclusions(string exclusions) => Exclusions = exclusions ?? string.Empty;
    public void SetCancellationPolicy(string? policy) => CancellationPolicy = policy;
    public void SetReschedulePolicy(string? policy) => ReschedulePolicy = policy;

    public void SetDuration(int durationMinutes) =>
        DurationMinutes = durationMinutes > 0 ? durationMinutes : throw new ArgumentOutOfRangeException(nameof(durationMinutes));

    public void SetSortOrder(int sortOrder) => SortOrder = sortOrder;

    public void SetSeo(string? title, string? metaDescription)
    {
        SeoTitle = title;
        SeoMetaDescription = metaDescription;
    }

    public void SetPricingType(ServicePricingType pricingType) => PricingType = pricingType;

    /// <summary>
    /// Replaces the meal rates of a meal-priced service. The listing-card
    /// price follows the cheapest meal, so it can never advertise a price no
    /// meal is actually sold at.
    /// </summary>
    public void SetMealOptions(IEnumerable<MealOption> options)
    {
        if (PricingType != ServicePricingType.MealBased)
        {
            throw new InvalidOperationException("Only a meal-priced service has meal rates.");
        }

        var list = options.OrderBy(o => o.Meal).ToList();
        MealPricing.ValidateOptions(list);
        _mealOptions = list;
        SetPrice(list.Min(o => o.BaseRate));
    }

    /// <summary>
    /// Sets the SRS 12.6.2/12.6.3 service-level option flags together - they
    /// are configured as one group in the admin edit form, so a single
    /// setter (mirroring <see cref="SetSeo"/>'s grouping) avoids seven
    /// separate calls at every call site.
    /// </summary>
    public void SetOptions(
        bool isTaxApplicable,
        bool isAddOnAllowed,
        bool isQuantityAllowed,
        bool isInspectionBased,
        bool isSlotRequired,
        bool isAddressRequired,
        bool isCustomerNoteAllowed,
        bool isDurationBased = false)
    {
        IsTaxApplicable = isTaxApplicable;
        IsAddOnAllowed = isAddOnAllowed;
        IsQuantityAllowed = isQuantityAllowed;
        IsInspectionBased = isInspectionBased;
        IsSlotRequired = isSlotRequired;
        IsAddressRequired = isAddressRequired;
        IsCustomerNoteAllowed = isCustomerNoteAllowed;
        IsDurationBased = isDurationBased;
    }

    public void Feature() => IsFeatured = true;
    public void Unfeature() => IsFeatured = false;

    /// <summary>
    /// Raised once by ServiceManagementService.UpdateAsync after every field
    /// setter for a general-field edit has applied, so
    /// CatalogCacheInvalidationHandler can evict the stale detail cache -
    /// mirroring SetPrice's own event for price-only changes, since a plain
    /// field edit otherwise raises nothing at all.
    /// </summary>
    public void MarkUpdated(Guid oldCategoryId) => RaiseDomainEvent(new ServiceUpdatedEvent(Id, oldCategoryId, CategoryId));

    public void Activate()
    {
        if (IsActive) return;
        IsActive = true;
        RaiseDomainEvent(new ServiceActivatedEvent(Id));
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;
        RaiseDomainEvent(new ServiceDeactivatedEvent(Id));
    }
}
