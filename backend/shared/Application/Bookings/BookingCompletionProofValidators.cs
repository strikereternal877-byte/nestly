using FluentValidation;

namespace Nestly.Application.Bookings;

public class SubmitCompletionProofRequestValidator : AbstractValidator<SubmitCompletionProofRequest>
{
    public SubmitCompletionProofRequestValidator()
    {
        RuleFor(x => x.PhotoRefs)
            .NotNull()
            .Must(refs => refs.Count > 0 && refs.All(r => !string.IsNullOrWhiteSpace(r)))
            .WithMessage("At least one non-empty photo reference is required.");

        RuleForEach(x => x.ChecklistAnswers).ChildRules(answer =>
        {
            answer.RuleFor(a => a.Item).NotEmpty().MaximumLength(500);
            answer.RuleFor(a => a.Notes).MaximumLength(1000);
        });
    }
}

/// <summary>Same shape as <c>RejectProviderKycDocumentRequestValidator</c>, required for the same reason: a rejection the provider cannot act on is just a disappearance.</summary>
public class RejectCompletionProofRequestValidator : AbstractValidator<RejectCompletionProofRequest>
{
    public RejectCompletionProofRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}
