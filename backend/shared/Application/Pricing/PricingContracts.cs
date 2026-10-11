using Nestly.Application.Meals;

namespace Nestly.Application.Pricing;

public record AddOnSelection(Guid AddOnId, int Quantity);

/// <summary>
/// Server-side price calculation input (task 48, SRS 11.9.2 - final price
/// must be calculated server-side). <see cref="ServiceVariantId"/> is null
/// for a service with no variants (Phase 3 catalog redesign) - the flat
/// <see cref="Nestly.Domain.Service.Price"/> applies exactly as before this
/// field existed.
/// </summary>
/// <remarks><see cref="Meals"/> is required for, and only accepted by, a MealBased service (docs/MEAL-SERVICE.md).</remarks>
public record PriceCalculationRequest(
    Guid ServiceId, Guid CityId, int Quantity, IReadOnlyList<AddOnSelection> AddOns, Guid? ServiceVariantId = null,
    MealSelection? Meals = null);

/// <summary><see cref="GroupId"/>/<see cref="GroupName"/> are null for an ungrouped add-on (today's default).</summary>
public record AddOnLineItem(
    Guid AddOnId, string Name, decimal UnitPrice, int Quantity, decimal LineTotal,
    Guid? GroupId = null, string? GroupName = null);

/// <summary>
/// Full price breakdown (SRS 11.9.1): base, add-ons, quantity, city-wise
/// price, visit charge, tax, fees. The <c>SelectedVariant*</c> fields are
/// null when no variant was selected (Phase 3 catalog redesign) - in which
/// case <see cref="BasePrice"/> is the service's flat price, unchanged from
/// before this field existed.
/// </summary>
public record PriceBreakdownResponse(
    decimal BasePrice,
    int Quantity,
    decimal BaseTotal,
    IReadOnlyList<AddOnLineItem> AddOnLineItems,
    decimal AddOnTotal,
    decimal VisitCharge,
    decimal Subtotal,
    decimal TaxPercentage,
    decimal TaxAmount,
    decimal PlatformFee,
    decimal TotalPayable,
    Guid? SelectedVariantId = null,
    string? SelectedVariantName = null,
    int? SelectedVariantDurationMinutes = null);
