using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Catalog;
using Nestly.Application.Coupons;
using Nestly.Application.Pricing;
using Nestly.Application.Serviceability;
using Nestly.Application.Settings;
using Nestly.Application.Slots;
using Nestly.Application.Subscriptions;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Results;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Booking summary/preview (SRS 11.7, task 57). Composes the existing
/// slot-availability and price-calculation services rather than
/// re-implementing their validation - a summary must reject exactly the same
/// combinations booking creation (task 58) will reject, or a customer could
/// see a summary that then fails to book.
/// </summary>
public class BookingSummaryService : IBookingSummaryService
{
    private readonly IServiceRepository _serviceRepository;
    private readonly IServiceAddOnRepository _addOnRepository;
    private readonly IServiceGroupRepository _serviceGroupRepository;
    private readonly ICustomerAddressRepository _addressRepository;
    private readonly ISlotAvailabilityService _slotAvailabilityService;
    private readonly IPriceCalculationService _priceCalculationService;
    private readonly ICouponService _couponService;
    private readonly ISubscriptionBenefitService _subscriptionBenefitService;
    private readonly IWalletService _walletService;
    private readonly IServiceabilityRepository _serviceabilityRepository;
    private readonly BookingOptions _bookingOptions;
    private readonly IPlatformRules _platformRules;

    public BookingSummaryService(
        IServiceRepository serviceRepository,
        IServiceAddOnRepository addOnRepository,
        IServiceGroupRepository serviceGroupRepository,
        ICustomerAddressRepository addressRepository,
        ISlotAvailabilityService slotAvailabilityService,
        IPriceCalculationService priceCalculationService,
        ICouponService couponService,
        ISubscriptionBenefitService subscriptionBenefitService,
        IWalletService walletService,
        IServiceabilityRepository serviceabilityRepository,
        IOptions<BookingOptions> bookingOptions,
        IPlatformRules? platformRules = null)
    {
        _platformRules = platformRules ?? NoPlatformRules.Instance;
        _serviceabilityRepository = serviceabilityRepository;
        _bookingOptions = bookingOptions.Value;
        _serviceRepository = serviceRepository;
        _addOnRepository = addOnRepository;
        _serviceGroupRepository = serviceGroupRepository;
        _addressRepository = addressRepository;
        _slotAvailabilityService = slotAvailabilityService;
        _priceCalculationService = priceCalculationService;
        _couponService = couponService;
        _subscriptionBenefitService = subscriptionBenefitService;
        _walletService = walletService;
    }

    public async Task<Result<BookingSummaryResponse>> GetSummaryAsync(Guid customerId, BookingSummaryRequest request)
    {
        if (request.Quantity <= 0)
        {
            return Error.Validation("Booking.InvalidQuantity", "Quantity must be positive.");
        }

        if (request.Quantity > _bookingOptions.MaxQuantityPerBooking)
        {
            return Error.Validation(
                "Booking.QuantityTooLarge",
                $"A single booking can be placed for at most {_bookingOptions.MaxQuantityPerBooking} units. Please split larger orders across bookings.");
        }

        var service = await _serviceRepository.GetByIdAsync(request.ServiceId);
        if (service is null || !service.IsActive)
        {
            return Error.NotFound("Booking.ServiceNotFound", "The specified service does not exist.");
        }

        if (!service.IsQuantityAllowed && request.Quantity != 1)
        {
            return Error.Validation("Booking.QuantityNotAllowed", "This service is booked one at a time.");
        }

        var address = await _addressRepository.GetByIdAsync(request.AddressId);
        if (address is null || address.CustomerId != customerId)
        {
            return Error.NotFound("Booking.AddressNotFound", "The specified address does not exist.");
        }

        // Everything downstream - serviceability, slots, city pricing - is
        // computed from the client-supplied LocalityId and CityId, while the
        // service is actually delivered to AddressId. Nothing tied those three
        // together, so a booking could be priced and slotted against one city
        // while being delivered to an address in another (the default address
        // is pre-selected for the customer, and nobody re-reads a default), and
        // a hand-crafted request could pick whichever CityId priced cheapest.
        var geographyResult = await ValidateGeographyAsync(request, address);
        if (geographyResult.IsFailure)
        {
            return geographyResult.Error;
        }

        var addOnSummaries = new List<ServiceAddOnSummaryResponse>(request.AddOns.Count);
        foreach (var selection in request.AddOns)
        {
            var addOn = await _addOnRepository.GetByIdAsync(selection.AddOnId);
            if (addOn is null || !addOn.IsActive || addOn.ServiceId != request.ServiceId)
            {
                return Error.Validation("Booking.InvalidAddOn", $"Add-on {selection.AddOnId} is not available for this service.");
            }

            addOnSummaries.Add(new ServiceAddOnSummaryResponse(addOn.Id, addOn.Name, addOn.Description, addOn.Price));
        }

        var availability = await _slotAvailabilityService.GetAvailableSlotsAsync(request.ServiceId, request.LocalityId, request.SlotDate);
        if (availability.IsFailure)
        {
            return availability.Error;
        }

        if (!availability.Value.IsServiceable)
        {
            return Error.Business("Booking.NotServiceable", "This service is not available at the selected address.");
        }

        var slot = availability.Value.Slots.FirstOrDefault(s => s.SlotWindowId == request.SlotWindowId);
        if (slot is null)
        {
            // "The slot is full" is one outcome with one code and one status
            // (409), whether it is caught here - because availability already
            // filtered the window out - or by the atomic reservation in
            // BookingService when two customers race for the last seat.
            // Anything else about the slot (cutoff, blackout, out of range) is
            // a business-rule failure, not contention.
            return availability.Value.Reason == SlotUnavailabilityReason.FullyBooked
                ? Error.Conflict("Booking.SlotCapacityReached", DescribeSlotUnavailability(availability.Value.Reason))
                : Error.Business("Booking.SlotNotAvailable", DescribeSlotUnavailability(availability.Value.Reason));
        }

        var priceResult = await _priceCalculationService.CalculateAsync(
            new PriceCalculationRequest(request.ServiceId, request.CityId, request.Quantity, request.AddOns, request.ServiceVariantId));
        if (priceResult.IsFailure)
        {
            return priceResult.Error;
        }

        CouponSummaryResponse? coupon = null;
        decimal finalPayable = priceResult.Value.TotalPayable;

        if (!string.IsNullOrWhiteSpace(request.CouponCode))
        {
            // Discount is calculated against the pre-tax Subtotal (SRS
            // 11.10.2's "min order amount" and percentage-discount rules
            // read most naturally against the goods/service value, not tax
            // and platform fees), then subtracted from TotalPayable (SRS
            // 14.1: "... + tax – discounts" - the discount comes off the
            // fully-loaded total, not the pre-tax subtotal).
            var couponResult = await _couponService.ValidateAsync(customerId, request.CouponCode, service.CategoryId, priceResult.Value.Subtotal);
            if (couponResult.IsFailure)
            {
                return couponResult.Error;
            }

            coupon = couponResult.Value;
            finalPayable = Math.Max(0, priceResult.Value.TotalPayable - coupon.DiscountAmount);
        }

        // Subscription benefit (task 179): automatic, needs no code from the
        // customer - but only when no coupon was applied. A coupon is an
        // explicit customer choice; stacking it with a standing subscription
        // benefit would need a documented precedence/combination rule this
        // spec never asks for (PRODUCT-ENHANCEMENTS.md #1), so the simplest
        // unambiguous reading is "the two are mutually exclusive per
        // booking," matching how <see cref="Coupon"/> already reads unique
        // per booking.
        SubscriptionBenefitSummary? subscriptionBenefit = null;
        if (coupon is null)
        {
            subscriptionBenefit = await _subscriptionBenefitService.PreviewAsync(customerId, finalPayable);
            if (subscriptionBenefit is not null)
            {
                finalPayable = Math.Max(0, finalPayable - subscriptionBenefit.DiscountAmount);
            }
        }

        // Task 310 (SRS 11.7.2): wallet is applied last, against whatever is
        // still payable after coupon/subscription - unlike those two, it
        // stacks with either. The balance itself is always surfaced (the
        // customer must see it before deciding to use it); the applied
        // amount is only non-zero when the caller opted in, and is capped at
        // both the available balance and what remains payable so the
        // customer's wallet can never be drawn down further than the
        // booking actually costs.
        decimal walletBalance = (await _walletService.GetBalanceAsync(customerId)).Value.Balance;
        decimal walletApplied = 0m;
        if (request.ApplyWalletCredit)
        {
            // Wallet rules: a cap on how much of one booking the wallet may pay for. Rounded down to the paisa, so the share
            // the gateway is asked for is never short by a rounding hair; 100 (the seeded value) is no cap at all.
            decimal walletShare = finalPayable;
            var walletRules = await _platformRules.GetWalletAsync();
            if (walletRules is not null && walletRules.MaxWalletUsagePercentagePerBooking < 100m)
            {
                walletShare = Math.Round(
                    finalPayable * walletRules.MaxWalletUsagePercentagePerBooking / 100m, 2, MidpointRounding.ToZero);
            }

            walletApplied = Math.Min(walletBalance, walletShare);
            finalPayable = Math.Max(0, finalPayable - walletApplied);
        }

        // Appliance/Service Group catalog redesign: inherited from the
        // service, never a customer selection - unlike the variant above,
        // there's nothing to validate against the request, just a name to
        // resolve for display/snapshot purposes.
        string? serviceGroupName = null;
        if (service.ServiceGroupId is Guid serviceGroupId)
        {
            serviceGroupName = (await _serviceGroupRepository.GetByIdAsync(serviceGroupId))?.Name;
        }

        var response = new BookingSummaryResponse(
            new BookingServiceSummary(
                service.Id, service.Name, service.Slug,
                priceResult.Value.SelectedVariantId, priceResult.Value.SelectedVariantName, priceResult.Value.SelectedVariantDurationMinutes,
                service.ServiceGroupId, serviceGroupName,
                service.DurationMinutes, service.IsDurationBased),
            addOnSummaries,
            new BookingAddressSummary(
                address.Id, address.Label, address.Line1, address.Line2, address.Landmark,
                address.Pincode, address.City, address.State, address.Latitude, address.Longitude,
                address.ContactName, address.ContactMobile),
            new BookingSlotSummary(slot.SlotWindowId, slot.Name, request.SlotDate, slot.StartTime, slot.EndTime),
            priceResult.Value,
            service.CancellationPolicy,
            service.ReschedulePolicy,
            coupon,
            finalPayable,
            subscriptionBenefit,
            new WalletCreditSummaryResponse(walletBalance, walletApplied));

        return Result.Success(response);
    }

    /// <summary>
    /// Ties the three geography inputs of a booking request together: the
    /// locality the slots and serviceability are computed from, the city the
    /// price is computed from, and the address the professional is actually
    /// sent to. All three are supplied by the client and were previously
    /// trusted independently.
    ///
    /// The locality is the anchor - its city and pincode are resolved
    /// server-side, and both of the other two must agree with it.
    /// </summary>
    private async Task<Result> ValidateGeographyAsync(BookingSummaryRequest request, Domain.CustomerAddress address)
    {
        var geography = await _serviceabilityRepository.GetCityAndPincodeForLocalityAsync(request.LocalityId);
        if (geography is null)
        {
            return Result.Failure(Error.NotFound("Booking.LocalityNotFound", "The specified locality does not exist."));
        }

        var (localityCityId, localityPincodeId) = geography.Value;

        if (request.CityId != localityCityId)
        {
            // City drives city-specific pricing (ServiceCityPrice), so an
            // unchecked CityId is a price-manipulation vector, not just an
            // inconsistency.
            return Result.Failure(Error.Validation(
                "Booking.CityMismatch",
                "The selected city does not match the selected locality."));
        }

        // PincodeId is resolved from the address's free-text pincode when the
        // address is saved (CustomerAddress.LinkToGeography). Null means that
        // pincode matched no active pincode in the geography master, so there
        // is no service area this address belongs to.
        if (address.PincodeId is null)
        {
            return Result.Failure(Error.Business(
                "Booking.AddressNotServiceable",
                "We don't serve this address's pincode yet. Please choose or add another address."));
        }

        if (address.PincodeId != localityPincodeId)
        {
            return Result.Failure(Error.Business(
                "Booking.AddressOutsideSelectedArea",
                $"The selected address ({address.City} {address.Pincode}) is outside the area you're booking in. Choose an address in this area, or switch your location."));
        }

        return Result.Success();
    }

    /// <summary>
    /// Customer-facing wording for a slot that isn't in the available set,
    /// matching <c>SlotAvailabilityService.DescribeUnavailability</c> - a
    /// booking blocked because the day is full should not read the same as
    /// one blocked because bookings closed an hour ago.
    /// </summary>
    private static string DescribeSlotUnavailability(SlotUnavailabilityReason reason) => reason switch
    {
        SlotUnavailabilityReason.FullyBooked => "This slot has just been fully booked. Please choose another.",
        SlotUnavailabilityReason.CutoffPassed => "Bookings for this slot have closed. Please choose a later slot.",
        SlotUnavailabilityReason.Blackout => "We aren't taking bookings on this date. Please choose another day.",
        SlotUnavailabilityReason.DateOutOfBookableRange => "This date is outside the window we can book. Please choose another day.",
        _ => "The selected slot is no longer available.",
    };
}
