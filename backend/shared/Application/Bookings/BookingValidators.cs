using FluentValidation;

namespace Nestly.Application.Bookings;

public class BookingSummaryRequestValidator : AbstractValidator<BookingSummaryRequest>
{
    public BookingSummaryRequestValidator()
    {
        RuleFor(x => x.ServiceId).NotEmpty();
        RuleFor(x => x.CityId).NotEmpty();
        RuleFor(x => x.AddressId).NotEmpty();
        RuleFor(x => x.LocalityId).NotEmpty();
        RuleFor(x => x.SlotWindowId).NotEmpty();
        // Upper bound is enforced in BookingSummaryService against the
        // configured Booking:MaxQuantityPerBooking - the business limit lives
        // with the business rule. This is the structural floor only.
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleForEach(x => x.AddOns).ChildRules(addOn =>
        {
            addOn.RuleFor(a => a.AddOnId).NotEmpty();
            addOn.RuleFor(a => a.Quantity).GreaterThan(0);
        });
        RuleFor(x => x.CouponCode).MaximumLength(50).When(x => x.CouponCode is not null);
        RuleFor(x => x.MealPreferences!).SetValidator(new Meals.MealPreferencesValidator()).When(x => x.MealPreferences is not null);
    }
}
