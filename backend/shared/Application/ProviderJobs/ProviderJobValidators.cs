using FluentValidation;

namespace Nestly.Application.ProviderJobs;

public class RejectJobRequestValidator : AbstractValidator<RejectJobRequest>
{
    public RejectJobRequestValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(1000);
    }
}

public class UploadJobCompletionProofRequestValidator : AbstractValidator<UploadJobCompletionProofRequest>
{
    public UploadJobCompletionProofRequestValidator()
    {
        RuleFor(x => x.ProofRef).NotEmpty().MaximumLength(500);
    }
}
