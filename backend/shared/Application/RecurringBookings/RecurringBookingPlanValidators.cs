using FluentValidation;
using Nestly.Domain;

namespace Nestly.Application.RecurringBookings;

public class CreateRecurringBookingPlanRequestValidator : AbstractValidator<CreateRecurringBookingPlanRequest>
{
    public CreateRecurringBookingPlanRequestValidator()
    {
        RuleFor(x => x.ServiceId).NotEmpty();
        RuleFor(x => x.CityId).NotEmpty();
        RuleFor(x => x.AddressId).NotEmpty();
        RuleFor(x => x.LocalityId).NotEmpty();
        RuleFor(x => x.SlotWindowId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);

        RuleFor(x => x.StartDate).GreaterThanOrEqualTo(x => DateOnly.FromDateTime(DateTime.UtcNow.Date))
            .WithMessage("Start date cannot be in the past.");

        RuleFor(x => x.RecurrenceDayOfWeek)
            .NotNull()
            .WithMessage("A day of week is required for a weekly or biweekly plan.")
            .When(x => x.Frequency is RecurringBookingRecurrenceFrequency.Weekly or RecurringBookingRecurrenceFrequency.Biweekly);

        RuleFor(x => x.RecurrenceDayOfWeek)
            .Null()
            .WithMessage("A day of week must not be set for a daily or monthly plan.")
            .When(x => x.Frequency is RecurringBookingRecurrenceFrequency.Daily or RecurringBookingRecurrenceFrequency.Monthly);

        RuleFor(x => x.RecurrenceDayOfMonth)
            .NotNull().InclusiveBetween(1, 31)
            .WithMessage("A day of month between 1 and 31 is required for a monthly plan.")
            .When(x => x.Frequency == RecurringBookingRecurrenceFrequency.Monthly);

        RuleFor(x => x.RecurrenceDayOfMonth)
            .Null()
            .WithMessage("A day of month must not be set for a daily, weekly or biweekly plan.")
            .When(x => x.Frequency is not RecurringBookingRecurrenceFrequency.Monthly);

        RuleFor(x => x.EndDate)
            .GreaterThanOrEqualTo(x => x.StartDate)
            .When(x => x.EndDate.HasValue)
            .WithMessage("End date cannot be before the start date.");

        RuleFor(x => x.OccurrenceCount).GreaterThan(0).When(x => x.OccurrenceCount.HasValue);

        RuleFor(x => x.LeadBookingId)
            .NotEmpty()
            .WithMessage("A prepaid plan needs the booking it is being bought with.")
            .When(x => x.PrepaidUpfront);

        RuleFor(x => x.AutoChargeEnabled)
            .Equal(false)
            .WithMessage("A prepaid plan is paid for at checkout, so auto-charge does not apply to it.")
            .When(x => x.PrepaidUpfront);

        RuleFor(x => x.ApplyWalletCredit)
            .Equal(false)
            .WithMessage("A prepaid plan is paid for in one checkout, so per-visit wallet credit does not apply to it.")
            .When(x => x.PrepaidUpfront);

        RuleForEach(x => x.AddOns).ChildRules(addOn =>
        {
            addOn.RuleFor(a => a.AddOnId).NotEmpty();
            addOn.RuleFor(a => a.Quantity).GreaterThan(0);
        });
    }
}
