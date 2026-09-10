using Nestly.Application;
using Nestly.Application.Abstractions.Caching;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Server-side price calculation (tasks 47a-c, 48). Base + add-ons +
/// quantity (47a-c) come straight off Service/ServiceAddOn; city-wise price
/// and visit charge/tax/fee (47d-g) apply on top. Coupons, wallet
/// deduction, and cancellation/reschedule fees (SRS 11.9.1) are out of
/// scope here - they belong to Payments/Post-Booking (Phase 4/5), which
/// don't exist yet.
///
/// Read-cached (catalog/pricing response-time fix): a successful result is
/// cached for <see cref="PriceCalculationTtl"/>, keyed on every input the
/// calculation reads (<see cref="CacheKeys.PriceCalculation"/>). Deliberately
/// TTL-only, like <c>CacheKeys.CategoriesInCity</c> - the inputs span too
/// many entities (service, variant, every selected add-on, city pricing
/// policy, city price override) to invalidate precisely without a fan-out
/// this hot a path shouldn't pay for, and a wrong price briefly surviving a
/// same-service price edit is bounded by a short TTL rather than open-ended.
/// A failed calculation (validation/not-found/business error) is never
/// cached - only ever the priced-out success case.
/// </summary>
public class PriceCalculationService : IPriceCalculationService
{
    /// <summary>Upper bound on a unit-measured service's quantity - a guardrail against a runaway value inflating a total, not a per-service limit (that would live on Service if the business needed one).</summary>
    private const int MaxQuantity = 99;

    /// <summary>
    /// Short enough that a price/policy edit is reflected well within a
    /// customer's checkout session, long enough to absorb the repeated
    /// recalculation a booking-summary screen triggers as the customer
    /// tweaks quantity/add-ons/slot (the "pricing/calculate took 2.8s-2.9s"
    /// complaint this fix targets).
    /// </summary>
    private static readonly TimeSpan PriceCalculationTtl = TimeSpan.FromSeconds(45);

    private readonly IServiceRepository _serviceRepository;
    private readonly IServiceAddOnRepository _addOnRepository;
    private readonly IServiceabilityRepository _serviceabilityRepository;
    private readonly IServiceCityPriceRepository _cityPriceRepository;
    private readonly ICityPricingPolicyRepository _pricingPolicyRepository;
    private readonly IServiceVariantRepository _variantRepository;
    private readonly IServiceAddOnGroupRepository _groupRepository;
    private readonly ICacheService _cache;

    public PriceCalculationService(
        IServiceRepository serviceRepository,
        IServiceAddOnRepository addOnRepository,
        IServiceabilityRepository serviceabilityRepository,
        IServiceCityPriceRepository cityPriceRepository,
        ICityPricingPolicyRepository pricingPolicyRepository,
        IServiceVariantRepository variantRepository,
        IServiceAddOnGroupRepository groupRepository,
        ICacheService cache)
    {
        _serviceRepository = serviceRepository;
        _addOnRepository = addOnRepository;
        _serviceabilityRepository = serviceabilityRepository;
        _cityPriceRepository = cityPriceRepository;
        _pricingPolicyRepository = pricingPolicyRepository;
        _variantRepository = variantRepository;
        _groupRepository = groupRepository;
        _cache = cache;
    }

    public async Task<Result<PriceBreakdownResponse>> CalculateAsync(PriceCalculationRequest request)
    {
        if (request.Quantity <= 0)
        {
            return Error.Validation("Pricing.InvalidQuantity", "Quantity must be positive.");
        }

        string cacheKey = CacheKeys.PriceCalculation(
            request.ServiceId,
            request.CityId,
            request.ServiceVariantId,
            request.Quantity,
            request.AddOns.Select(a => (a.AddOnId, a.Quantity)));

        var cached = await _cache.GetAsync<PriceBreakdownResponse>(cacheKey);
        if (cached is not null)
        {
            return Result.Success(cached);
        }

        var result = await ComputeAsync(request);
        if (result.IsSuccess)
        {
            await _cache.SetAsync(cacheKey, result.Value, PriceCalculationTtl);
        }

        return result;
    }

    private async Task<Result<PriceBreakdownResponse>> ComputeAsync(PriceCalculationRequest request)
    {
        var service = await _serviceRepository.GetByIdAsync(request.ServiceId);
        if (service is null || !service.IsActive)
        {
            return Error.NotFound("Pricing.ServiceNotFound", "The specified service does not exist.");
        }

        // Quantity is only meaningful for services measured in units (AC units,
        // rooms, seats - Service.IsQuantityAllowed). For everything else it is
        // forced to 1 here rather than trusted from the request: the price is
        // calculated server-side (SRS 11.9), so a client that sends a quantity
        // for a flat-rate service - a UI bug or deliberate tampering - must
        // never be able to multiply the base price. Allowed quantities are
        // still capped so a runaway value can't inflate a total unbounded.
        int effectiveQuantity = service.IsQuantityAllowed
            ? Math.Min(request.Quantity, MaxQuantity)
            : 1;

        if (!await _serviceabilityRepository.CityExistsAsync(request.CityId))
        {
            return Error.NotFound("Pricing.CityNotFound", "The specified city does not exist.");
        }

        // Phase 3 catalog redesign: a selected variant's own price/duration
        // takes over from the service's flat Price - null when the caller
        // never supplies a variant id, the default/unchanged path.
        ServiceVariant? selectedVariant = null;
        if (request.ServiceVariantId is Guid variantId)
        {
            selectedVariant = await _variantRepository.GetByIdAsync(variantId);
            if (selectedVariant is null || !selectedVariant.IsActive || selectedVariant.ServiceId != request.ServiceId)
            {
                return Error.NotFound("Pricing.VariantNotFound", "The specified variant is not available for this service.");
            }
        }

        var selectedAddOns = new List<ServiceAddOn>(request.AddOns.Count);
        foreach (var selection in request.AddOns)
        {
            if (selection.Quantity <= 0)
            {
                return Error.Validation("Pricing.InvalidAddOnQuantity", "Add-on quantity must be positive.");
            }

            var addOn = await _addOnRepository.GetByIdAsync(selection.AddOnId);
            if (addOn is null || !addOn.IsActive || addOn.ServiceId != request.ServiceId)
            {
                return Error.Validation("Pricing.InvalidAddOn", $"Add-on {selection.AddOnId} is not available for this service.");
            }

            selectedAddOns.Add(addOn);
        }

        // Phase 3 catalog redesign: validate pick-one/pick-many group rules
        // before computing totals. Add-ons with no GroupId (today's default)
        // are never checked - see AddOnGroupSelectionRules' doc comment.
        var groupIds = selectedAddOns.Where(a => a.GroupId is not null).Select(a => a.GroupId!.Value).Distinct().ToList();
        var groupsById = await _groupRepository.GetByIdsAsync(groupIds);
        var ruleValidation = AddOnGroupSelectionRules.Validate(selectedAddOns, groupsById);
        if (ruleValidation.IsFailure)
        {
            return ruleValidation.Error;
        }

        var addOnLineItems = new List<AddOnLineItem>(selectedAddOns.Count);
        foreach (var (addOn, selection) in selectedAddOns.Zip(request.AddOns))
        {
            decimal lineTotal = addOn.Price * selection.Quantity;
            string? groupName = addOn.GroupId is Guid gid && groupsById.TryGetValue(gid, out var g) ? g.Name : null;
            addOnLineItems.Add(new AddOnLineItem(addOn.Id, addOn.Name, addOn.Price, selection.Quantity, lineTotal, addOn.GroupId, groupName));
        }

        // City price overrides apply to the service's flat price only (Phase
        // 3 catalog redesign scope boundary) - a selected variant's own
        // price is definitive and is never adjusted per city.
        decimal basePrice;
        if (selectedVariant is not null)
        {
            basePrice = selectedVariant.Price;
        }
        else
        {
            var cityOverride = await _cityPriceRepository.GetForServiceAndCityAsync(request.ServiceId, request.CityId);
            basePrice = cityOverride?.Price ?? service.Price;
        }

        decimal baseTotal = basePrice * effectiveQuantity;
        decimal addOnTotal = addOnLineItems.Sum(a => a.LineTotal);

        var pricingPolicy = await _pricingPolicyRepository.GetByCityAsync(request.CityId);
        decimal visitCharge = pricingPolicy?.VisitCharge ?? 0m;
        decimal taxPercentage = pricingPolicy?.TaxPercentage ?? 0m;
        decimal platformFee = pricingPolicy?.PlatformFee ?? 0m;

        decimal subtotal = baseTotal + addOnTotal + visitCharge;
        // MidpointRounding stated explicitly (task 260): this is money, and
        // ToEven matches CommissionCalculator/CancellationFeeCalculator/
        // RescheduleFeeCalculator rather than leaving the rule to a language default.
        decimal taxAmount = Math.Round(subtotal * taxPercentage / 100m, 2, MidpointRounding.ToEven);
        decimal totalPayable = subtotal + taxAmount + platformFee;

        return new PriceBreakdownResponse(
            basePrice,
            effectiveQuantity,
            baseTotal,
            addOnLineItems,
            addOnTotal,
            visitCharge,
            subtotal,
            taxPercentage,
            taxAmount,
            platformFee,
            totalPayable,
            selectedVariant?.Id,
            selectedVariant?.Name,
            selectedVariant?.DurationMinutes);
    }
}
