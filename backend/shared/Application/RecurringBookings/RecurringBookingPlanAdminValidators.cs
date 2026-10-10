using FluentValidation;

namespace Nestly.Application.RecurringBookings;

/// <summary>Same paging bounds every other admin search validator enforces (see <c>CouponAdminSearchRequestValidator</c>) - the ceiling matches <c>PagedQueryExtensions.MaxPageSize</c>.</summary>
public class AdminRecurringPlanSearchRequestValidator : AbstractValidator<AdminRecurringPlanSearchRequest>
{
    public AdminRecurringPlanSearchRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

/// <summary>
/// The horizon's ordering is checked in the service rather than here, so it
/// returns the same "Reports.InvalidDateRange" business error every other
/// admin report answers with instead of a 400 from one screen and a 422 from
/// the next. This validator only rejects a window so long it could not have
/// been typed on purpose.
/// </summary>
public class AdminRecurringPlanReportRequestValidator : AbstractValidator<AdminRecurringPlanReportRequest>
{
    /// <summary>A year of daily buckets is already more than a screen can render; anything beyond it is a typo or a scrape.</summary>
    public const int MaxHorizonDays = 366;

    public AdminRecurringPlanReportRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => x.FromDate is not { } from || x.ToDate is not { } to || to.DayNumber - from.DayNumber <= MaxHorizonDays)
            .WithMessage($"The reporting horizon cannot span more than {MaxHorizonDays} days.")
            .OverridePropertyName(nameof(AdminRecurringPlanReportRequest.ToDate));
    }
}

/// <summary>A cancellation reason is required for the audit trail, same convention as <c>AdminCancelBookingRequestValidator</c>.</summary>
public class AdminCancelRecurringPlanRequestValidator : AbstractValidator<AdminCancelRecurringPlanRequest>
{
    public AdminCancelRecurringPlanRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}

/// <summary>A pause reason is required for the audit trail, same convention as <see cref="AdminCancelRecurringPlanRequestValidator"/>.</summary>
public class AdminPauseRecurringPlanRequestValidator : AbstractValidator<AdminPauseRecurringPlanRequest>
{
    public AdminPauseRecurringPlanRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}

/// <summary>A resume reason is required for the audit trail, same convention as <see cref="AdminCancelRecurringPlanRequestValidator"/>.</summary>
public class AdminResumeRecurringPlanRequestValidator : AbstractValidator<AdminResumeRecurringPlanRequest>
{
    public AdminResumeRecurringPlanRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}
