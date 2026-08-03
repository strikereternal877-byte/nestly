using FluentValidation;

namespace Nestly.Application.ProviderIdentity;

public class SubmitProviderKycDocumentRequestValidator : AbstractValidator<SubmitProviderKycDocumentRequest>
{
    public SubmitProviderKycDocumentRequestValidator()
    {
        RuleFor(x => x.ProviderId).NotEmpty();

        RuleFor(x => x.DocType).IsInEnum();

        RuleFor(x => x.FileRef)
            .NotEmpty().WithMessage("A file reference is required")
            .MaximumLength(1000);

        RuleFor(x => x.DocNumber).MaximumLength(100);
    }
}
