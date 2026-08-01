using FluentValidation;

namespace Nestly.Application.Referral;

/// <summary>Task 167 admin config PUT - mirrors <see cref="Domain.ReferralProgramConfig"/>'s own constructor/Update guard clauses so a bad request is rejected with a 400 instead of surfacing as an unhandled ArgumentOutOfRangeException.</summary>
public class ReferralProgramConfigUpdateRequestValidator : AbstractValidator<ReferralProgramConfigUpdateRequest>
{
    public ReferralProgramConfigUpdateRequestValidator()
    {
        RuleFor(x => x.ReferrerRewardValue).GreaterThan(0);
        RuleFor(x => x.RefereeRewardValue).GreaterThan(0);
        RuleFor(x => x.MinQualifyingOrderAmount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.ReferralExpiryDays).GreaterThan(0).LessThanOrEqualTo(3650);
        RuleFor(x => x.MaxReferralsPerCustomer).GreaterThan(0).When(x => x.MaxReferralsPerCustomer.HasValue);
    }
}

/// <summary>Task 174 admin milestone create.</summary>
public class ReferralMilestoneCreateRequestValidator : AbstractValidator<ReferralMilestoneCreateRequest>
{
    public ReferralMilestoneCreateRequestValidator()
    {
        RuleFor(x => x.ThresholdCount).GreaterThan(0).LessThanOrEqualTo(100_000);
        RuleFor(x => x.BonusValue).GreaterThan(0);
    }
}

/// <summary>Task 170 admin list/search - bounds paging the same way <c>CustomerSearchRequestValidator</c> does.</summary>
public class ReferralAdminSearchRequestValidator : AbstractValidator<ReferralAdminSearchRequest>
{
    public const int MaxPageSize = 100;

    public ReferralAdminSearchRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);
    }
}

/// <summary>Task 170 fraud approve/reject body - the note is optional context, but when supplied it's capped the same way every other free-text audit note in this codebase is (e.g. <c>AddCustomerNoteRequestValidator</c>).</summary>
public class FraudReviewActionRequestValidator : AbstractValidator<FraudReviewActionRequest>
{
    public FraudReviewActionRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(1000);
    }
}
