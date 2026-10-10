using FluentValidation;
using Nestly.Domain;

namespace Nestly.Application.ProviderManagement;

/// <summary>
/// Bounds paging, mirroring <c>CustomerSearchRequestValidator</c> (task
/// 150a). The created-date range rule mirrors
/// <c>AdminBookingSearchRequestValidator</c>'s identical guard (Provider
/// Onboarding Overview dashboard) - a caller cannot request an inverted
/// window.
/// </summary>
public class ProviderSearchRequestValidator : AbstractValidator<ProviderSearchRequest>
{
    public const int MaxPageSize = 100;

    public ProviderSearchRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);

        RuleFor(x => x.CreatedToUtc)
            .GreaterThanOrEqualTo(x => x.CreatedFromUtc!.Value)
            .When(x => x.CreatedFromUtc.HasValue && x.CreatedToUtc.HasValue)
            .WithMessage("Created-to date must be on or after the created-from date.");
    }
}

/// <summary>Bounds paging/window for the performance ranking list, mirroring <see cref="ProviderSearchRequestValidator"/>.</summary>
public class ProviderPerformanceListRequestValidator : AbstractValidator<ProviderPerformanceListRequest>
{
    public const int MaxPageSize = 100;
    public const int MaxPeriodDays = 365;

    public ProviderPerformanceListRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);
        RuleFor(x => x.PeriodDays).InclusiveBetween(1, MaxPeriodDays);
        RuleFor(x => x.SortBy).IsInEnum();
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

        // Task 243: same bounds as UpsertAddressRequestValidator's Latitude/Longitude.
        RuleFor(x => x.Latitude).InclusiveBetween(-90m, 90m).When(x => x.Latitude.HasValue);
        RuleFor(x => x.Longitude).InclusiveBetween(-180m, 180m).When(x => x.Longitude.HasValue);
        RuleFor(x => x.Longitude).NotNull().WithMessage("Longitude is required when latitude is set.").When(x => x.Latitude.HasValue);
        RuleFor(x => x.Latitude).NotNull().WithMessage("Latitude is required when longitude is set.").When(x => x.Longitude.HasValue);
    }
}

/// <summary>Mirrors <see cref="ProviderCapacity.SetLimits"/>'s own invariant (positive when set) so a bad value 400s here instead of round-tripping to the domain exception.</summary>
public class SetProviderCapacityRequestValidator : AbstractValidator<SetProviderCapacityRequest>
{
    public SetProviderCapacityRequestValidator()
    {
        RuleFor(x => x.MaxJobsPerDay).GreaterThan(0).When(x => x.MaxJobsPerDay.HasValue);
        RuleFor(x => x.MaxJobsPerSlot).GreaterThan(0).When(x => x.MaxJobsPerSlot.HasValue);
    }
}

public class SuspendProviderRequestValidator : AbstractValidator<SuspendProviderRequest>
{
    public SuspendProviderRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}

public class DeleteProviderRequestValidator : AbstractValidator<DeleteProviderRequest>
{
    public DeleteProviderRequestValidator()
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

/// <summary>Task 293. Same shape as the KYC rejection above, and required for the same reason: a rejection the provider cannot act on is just a disappearance.</summary>
public class RejectProviderPhotoRequestValidator : AbstractValidator<RejectProviderPhotoRequest>
{
    public RejectProviderPhotoRequestValidator()
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

/// <summary>Real PayU Payouts transfer webhook's raw JSON body - mirrors <c>PayUWebhookFormPayloadValidator</c>'s shallow "enough to normalize safely" scope, not full business validation (that lives in <c>ProviderPayoutService</c>).</summary>
public class PayUPayoutWebhookPayloadValidator : AbstractValidator<PayUPayoutWebhookPayload>
{
    public PayUPayoutWebhookPayloadValidator()
    {
        RuleFor(x => x.Event).NotEmpty().MaximumLength(50);
        RuleFor(x => x.MerchantReferenceId).NotEmpty().MaximumLength(100);
    }
}

// ---- Bank account (structured payout details, OPEN DECISIONS #3) ----

/// <summary>
/// <see cref="SubmitProviderBankAccountRequest.IfscCode"/> must match the
/// standard 11-character Indian IFSC format: 4 letters (bank code), a literal
/// '0' (reserved for future use), then 6 alphanumeric characters (branch
/// code). Account number is digits-only, 6-20 characters - the common
/// practical range across Indian banks (no single universal length).
/// </summary>
public class SubmitProviderBankAccountRequestValidator : AbstractValidator<SubmitProviderBankAccountRequest>
{
    private const string IfscPattern = "^[A-Z]{4}0[A-Z0-9]{6}$";
    private const string AccountNumberPattern = "^[0-9]{6,20}$";

    public SubmitProviderBankAccountRequestValidator()
    {
        RuleFor(x => x.ProviderId).NotEmpty();

        RuleFor(x => x.AccountHolderName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.BankName).NotEmpty().MaximumLength(200);

        RuleFor(x => x.AccountNumber)
            .NotEmpty()
            .Matches(AccountNumberPattern)
            .WithMessage("Account number must be 6 to 20 digits.");

        RuleFor(x => x.IfscCode)
            .NotEmpty()
            .Matches(IfscPattern)
            .WithMessage("IFSC code must be 11 characters in the standard format (e.g. HDFC0001234).");
    }
}

public class RejectProviderBankAccountRequestValidator : AbstractValidator<RejectProviderBankAccountRequest>
{
    public RejectProviderBankAccountRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}
