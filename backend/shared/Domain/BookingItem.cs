using Nestly.BuildingBlocks.Primitives;
using Nestly.Domain.Meals;

namespace Nestly.Domain;

/// <summary>
/// A service booked within a <see cref="Booking"/>, snapshotted at booking
/// time (SRS 23.3, task 59c) - name and price are copied from
/// <see cref="Service"/> as of the moment of booking. The cart model is
/// single-service for now (SRS 11.7.1, task 57), so a booking has exactly
/// one of these, but the schema is shaped to carry more than one without a
/// migration when that changes.
/// </summary>
public class BookingItem : Entity<Guid>
{
    private readonly List<BookingAddOnItem> _addOns = [];

    public Guid BookingId { get; private set; }

    /// <summary>Traceability only - not a foreign key. Deleting/deactivating the source service must never affect this row.</summary>
    public Guid ServiceId { get; private set; }

    public string NameSnapshot { get; private set; } = string.Empty;
    public string SlugSnapshot { get; private set; } = string.Empty;
    public decimal UnitPriceSnapshot { get; private set; }
    public int Quantity { get; private set; }
    public decimal LineTotalSnapshot { get; private set; }

    /// <summary>Traceability only - not a foreign key, same convention as <see cref="ServiceId"/>. Null when no variant was selected (Phase 3 catalog redesign) - the flat <see cref="Service"/> price/duration applied instead.</summary>
    public Guid? ServiceVariantId { get; private set; }
    public string? VariantNameSnapshot { get; private set; }
    public int? VariantDurationMinutesSnapshot { get; private set; }

    /// <summary>
    /// Traceability only - not a foreign key, same convention as
    /// <see cref="ServiceId"/>. Null when the service wasn't assigned to a
    /// <see cref="ServiceGroup"/> at booking time (Appliance/Service Group
    /// catalog redesign) - inherited from the service, never a customer
    /// selection, so it's captured here purely so a past booking's detail
    /// still shows which section the service was booked under even if the
    /// service is later re-grouped or ungrouped.
    /// </summary>
    public Guid? ServiceGroupId { get; private set; }
    public string? ServiceGroupNameSnapshot { get; private set; }

    /// <summary>
    /// A cook booking only (docs/MEAL-SERVICE.md): the meals cooked, as a
    /// <see cref="MealTypeExtensions.ToMask"/> bitmask (0 otherwise), for how
    /// many people, and the food preferences asked at booking time - what the
    /// cook needs, and what "keep this cook monthly" starts from.
    /// </summary>
    public int MealsMask { get; private set; }
    public int? PersonsSnapshot { get; private set; }
    public MealPreferences? MealPreferencesSnapshot { get; private set; }

    public IReadOnlyList<BookingAddOnItem> AddOns => _addOns;

    protected BookingItem() { }

    /// <summary>Records what a cook booking is for; see <see cref="MealsMask"/>.</summary>
    public void RecordMeals(IReadOnlyCollection<MealType> meals, int persons, MealPreferences? preferences)
    {
        if (meals.Count == 0)
        {
            throw new ArgumentException("Choose at least one meal.", nameof(meals));
        }

        if (persons is < 1 or > MealPricing.MaxPersons)
        {
            throw new ArgumentOutOfRangeException(nameof(persons), $"Cooking is for 1 to {MealPricing.MaxPersons} persons.");
        }

        MealsMask = MealTypeExtensions.ToMask(meals);
        PersonsSnapshot = persons;
        MealPreferencesSnapshot = preferences;
    }

    public BookingItem(Guid id, Guid bookingId, Guid serviceId, string nameSnapshot, string slugSnapshot, decimal unitPriceSnapshot, int quantity)
        : base(id)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        BookingId = bookingId;
        ServiceId = serviceId;
        NameSnapshot = nameSnapshot ?? throw new ArgumentNullException(nameof(nameSnapshot));
        SlugSnapshot = slugSnapshot ?? throw new ArgumentNullException(nameof(slugSnapshot));
        UnitPriceSnapshot = unitPriceSnapshot;
        Quantity = quantity;
        LineTotalSnapshot = unitPriceSnapshot * quantity;
    }

    /// <summary>Same as the base constructor, plus the variant snapshot fields (Phase 3 catalog redesign) - used when the booked service line was booked against a specific <see cref="ServiceVariant"/> rather than the service's flat price.</summary>
    public BookingItem(
        Guid id, Guid bookingId, Guid serviceId, string nameSnapshot, string slugSnapshot, decimal unitPriceSnapshot, int quantity,
        Guid? serviceVariantId, string? variantNameSnapshot, int? variantDurationMinutesSnapshot)
        : this(id, bookingId, serviceId, nameSnapshot, slugSnapshot, unitPriceSnapshot, quantity)
    {
        ServiceVariantId = serviceVariantId;
        VariantNameSnapshot = variantNameSnapshot;
        VariantDurationMinutesSnapshot = variantDurationMinutesSnapshot;
    }

    /// <summary>Same as the variant-carrying constructor, plus the service-group snapshot fields (Appliance/Service Group catalog redesign) - used when the booked service currently belongs to a <see cref="ServiceGroup"/>.</summary>
    public BookingItem(
        Guid id, Guid bookingId, Guid serviceId, string nameSnapshot, string slugSnapshot, decimal unitPriceSnapshot, int quantity,
        Guid? serviceVariantId, string? variantNameSnapshot, int? variantDurationMinutesSnapshot,
        Guid? serviceGroupId, string? serviceGroupNameSnapshot)
        : this(id, bookingId, serviceId, nameSnapshot, slugSnapshot, unitPriceSnapshot, quantity,
              serviceVariantId, variantNameSnapshot, variantDurationMinutesSnapshot)
    {
        ServiceGroupId = serviceGroupId;
        ServiceGroupNameSnapshot = serviceGroupNameSnapshot;
    }

    public BookingAddOnItem AddAddOn(Guid id, Guid serviceAddOnId, string nameSnapshot, decimal unitPriceSnapshot, int quantity)
    {
        var addOn = new BookingAddOnItem(id, Id, serviceAddOnId, nameSnapshot, unitPriceSnapshot, quantity);
        _addOns.Add(addOn);
        return addOn;
    }

    /// <summary>Same as <see cref="AddAddOn(Guid,Guid,string,decimal,int)"/>, plus the add-on-group snapshot fields (Phase 3 catalog redesign) - used when the selected add-on belonged to a <see cref="ServiceAddOnGroup"/>.</summary>
    public BookingAddOnItem AddAddOn(
        Guid id, Guid serviceAddOnId, string nameSnapshot, decimal unitPriceSnapshot, int quantity,
        Guid? addOnGroupId, string? groupNameSnapshot)
    {
        var addOn = new BookingAddOnItem(id, Id, serviceAddOnId, nameSnapshot, unitPriceSnapshot, quantity, addOnGroupId, groupNameSnapshot);
        _addOns.Add(addOn);
        return addOn;
    }
}
