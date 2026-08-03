using FluentValidation;

namespace Nestly.Application.ProviderAvailability;

public class ProviderAvailabilityWindowInputValidator : AbstractValidator<ProviderAvailabilityWindowInput>
{
    public ProviderAvailabilityWindowInputValidator()
    {
        RuleFor(x => x.DayOfWeek).IsInEnum();
        RuleFor(x => x.EndTime).GreaterThan(x => x.StartTime).WithMessage("The start time must be before the end time.");
    }
}

public class UpdateProviderAvailabilityWindowsRequestValidator : AbstractValidator<UpdateProviderAvailabilityWindowsRequest>
{
    public UpdateProviderAvailabilityWindowsRequestValidator()
    {
        RuleForEach(x => x.Windows).SetValidator(new ProviderAvailabilityWindowInputValidator());
    }
}

public class AddProviderBlackoutDateRequestValidator : AbstractValidator<AddProviderBlackoutDateRequest>
{
    public AddProviderBlackoutDateRequestValidator()
    {
        RuleFor(x => x.EndDate)
            .GreaterThanOrEqualTo(x => x.StartDate)
            .WithMessage("The start date must not be after the end date.");

        RuleFor(x => x.Reason).MaximumLength(500);
    }
}
