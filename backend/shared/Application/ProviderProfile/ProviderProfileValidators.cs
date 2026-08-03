using FluentValidation;

namespace Nestly.Application.ProviderProfile;

public class UpdateProviderProfileRequestValidator : AbstractValidator<UpdateProviderProfileRequest>
{
    public UpdateProviderProfileRequestValidator()
    {
        RuleFor(x => x.LegalName)
            .NotEmpty().WithMessage("Legal name is required")
            .MaximumLength(200);

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("Display name is required")
            .MaximumLength(200);

        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Email must be a valid email address")
            .When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public class ProviderServiceAreaInputValidator : AbstractValidator<ProviderServiceAreaInput>
{
    public ProviderServiceAreaInputValidator()
    {
        RuleFor(x => x.CityId).NotEmpty().WithMessage("A city is required for each service area");
    }
}

public class UpdateProviderServiceAreasRequestValidator : AbstractValidator<UpdateProviderServiceAreasRequest>
{
    public UpdateProviderServiceAreasRequestValidator()
    {
        RuleForEach(x => x.Areas).SetValidator(new ProviderServiceAreaInputValidator());
    }
}

public class ProviderSkillInputValidator : AbstractValidator<ProviderSkillInput>
{
    public ProviderSkillInputValidator()
    {
        RuleFor(x => x.CategoryId).NotEmpty().WithMessage("A category is required for each skill");
    }
}

public class UpdateProviderSkillsRequestValidator : AbstractValidator<UpdateProviderSkillsRequest>
{
    public UpdateProviderSkillsRequestValidator()
    {
        RuleForEach(x => x.Skills).SetValidator(new ProviderSkillInputValidator());
    }
}
