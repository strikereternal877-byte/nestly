using FluentValidation;
using Nestly.Domain;

namespace Nestly.Application.ProviderManagement;

/// <summary>Bounds paging, mirroring <c>CustomerSearchRequestValidator</c> (task 150a).</summary>
public class ProviderSearchRequestValidator : AbstractValidator<ProviderSearchRequest>
{
    public const int MaxPageSize = 100;

    public ProviderSearchRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);
    }
}

public class CreateProviderRequestValidator : AbstractValidator<CreateProviderRequest>
{
    public CreateProviderRequestValidator()
    {
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Email).MaximumLength(200).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public class UpdateProviderRequestValidator : AbstractValidator<UpdateProviderRequest>
{
    public UpdateProviderRequestValidator()
    {
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).MaximumLength(200).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public class SuspendProviderRequestValidator : AbstractValidator<SuspendProviderRequest>
{
    public SuspendProviderRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}

public class RejectProviderKycDocumentRequestValidator : AbstractValidator<RejectProviderKycDocumentRequest>
{
    public RejectProviderKycDocumentRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}

public class RecordBackgroundCheckRequestValidator : AbstractValidator<RecordBackgroundCheckRequest>
{
    public RecordBackgroundCheckRequestValidator()
    {
        RuleFor(x => x.Status).NotEqual(ProviderBackgroundCheckStatus.Pending)
            .WithMessage("A background check must be recorded with a final Passed/Failed outcome.");
        RuleFor(x => x.Notes).MaximumLength(1000);
    }
}

public class AssignProviderRequestValidator : AbstractValidator<AssignProviderRequest>
{
    public AssignProviderRequestValidator()
    {
        RuleFor(x => x.ProviderId).NotEmpty();
    }
}

public class RejectAssignmentRequestValidator : AbstractValidator<RejectAssignmentRequest>
{
    public RejectAssignmentRequestValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(1000);
    }
}

public class RecordProviderEarningAdjustmentRequestValidator : AbstractValidator<RecordProviderEarningAdjustmentRequest>
{
    public RecordProviderEarningAdjustmentRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(300);
    }
}

public class CreateProviderPayoutRequestValidator : AbstractValidator<CreateProviderPayoutRequest>
{
    public CreateProviderPayoutRequestValidator()
    {
        RuleFor(x => x.PeriodEnd)
            .GreaterThanOrEqualTo(x => x.PeriodStart)
            .WithMessage("Payout period end cannot be before its start.");
    }
}

public class UpdateProviderPayoutStatusRequestValidator : AbstractValidator<UpdateProviderPayoutStatusRequest>
{
    public UpdateProviderPayoutStatusRequestValidator()
    {
        RuleFor(x => x.PayoutReference).MaximumLength(100);
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}
