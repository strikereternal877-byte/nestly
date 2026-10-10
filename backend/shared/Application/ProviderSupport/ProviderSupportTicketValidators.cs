using FluentValidation;

namespace Nestly.Application.ProviderSupport;

public class CreateProviderSupportTicketRequestValidator : AbstractValidator<CreateProviderSupportTicketRequest>
{
    public CreateProviderSupportTicketRequestValidator()
    {
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(4000);
    }
}

public class AddProviderSupportTicketCommentRequestValidator : AbstractValidator<AddProviderSupportTicketCommentRequest>
{
    public AddProviderSupportTicketCommentRequestValidator()
    {
        RuleFor(x => x.Comment).NotEmpty().MaximumLength(2000);
    }
}

public class ResolveProviderSupportTicketRequestValidator : AbstractValidator<ResolveProviderSupportTicketRequest>
{
    public ResolveProviderSupportTicketRequestValidator()
    {
        RuleFor(x => x.ResolutionSummary).NotEmpty().MaximumLength(2000);
    }
}

public class AdminProviderSupportTicketSearchRequestValidator : AbstractValidator<AdminProviderSupportTicketSearchRequest>
{
    public const int MaxPageSize = 100;

    public AdminProviderSupportTicketSearchRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);

        RuleFor(x => x.ToUtc)
            .GreaterThanOrEqualTo(x => x.FromUtc!.Value)
            .When(x => x.FromUtc.HasValue && x.ToUtc.HasValue)
            .WithMessage("The to-date must be on or after the from-date.");
    }
}
