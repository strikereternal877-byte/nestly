using System.Globalization;
using FluentValidation;
using Nestly.Domain.MonthlyService;

namespace Nestly.Application.MonthlyService;

public static class MonthlyServiceTimeFormat
{
    public const string Pattern = "HH:mm";

    public static bool TryParse(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, Pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static string Format(TimeOnly time) => time.ToString(Pattern, CultureInfo.InvariantCulture);
}

public class MonthlyServicePlanUpsertRequestValidator : AbstractValidator<MonthlyServicePlanUpsertRequest>
{
    public MonthlyServicePlanUpsertRequestValidator()
    {
        RuleFor(x => x.ServiceId).NotEmpty();
        RuleFor(x => x.CityId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Basis).IsInEnum();
        RuleFor(x => x.HoursPerVisit)
            .NotNull().WithMessage("Hours per visit is required for an hourly plan.")
            .GreaterThan(0).LessThanOrEqualTo(12)
            .When(x => x.Basis == MonthlyServicePlanBasis.Hourly);
        RuleFor(x => x.IncludedTasks)
            .Must(t => t is not null && t.Any(s => !string.IsNullOrWhiteSpace(s)))
            .WithMessage("List at least one task for a task-based plan.")
            .When(x => x.Basis == MonthlyServicePlanBasis.TaskBased);
        RuleFor(x => x.IncludedTasks)
            .Must(t => t is null || t.Count <= MonthlyServicePlan.MaxIncludedTasks)
            .WithMessage($"At most {MonthlyServicePlan.MaxIncludedTasks} tasks.");
        RuleForEach(x => x.IncludedTasks).MaximumLength(MonthlyServicePlan.MaxTaskLength);
        RuleFor(x => x.RatePerVisit).GreaterThan(0).LessThanOrEqualTo(100_000);
        RuleFor(x => x.CommissionPercent).InclusiveBetween(0, MonthlyServicePlan.MaxCommissionPercent);
        RuleFor(x => x.Frequency).IsInEnum();
        RuleFor(x => x.TimesPerPeriod)
            .NotNull().WithMessage("How many times a week?")
            .InclusiveBetween(1, 7)
            .When(x => x.Frequency == MonthlyServiceFrequency.TimesPerWeek);
        RuleFor(x => x.TimesPerPeriod)
            .NotNull().WithMessage("How many times a month?")
            .InclusiveBetween(1, MonthDays.MaxDay)
            .When(x => x.Frequency == MonthlyServiceFrequency.TimesPerMonth);
    }
}

public class MonthlyServiceContractRequestValidator : AbstractValidator<MonthlyServiceContractRequest>
{
    public MonthlyServiceContractRequestValidator()
    {
        RuleFor(x => x.PlanId).NotEmpty();
        RuleFor(x => x.AddressId).NotEmpty();
        // Days are required unless the plan is per-month (dates instead) - the
        // service checks the exact count against the plan, which this
        // validator cannot see.
        RuleFor(x => x.Days).NotNull();
        RuleFor(x => x).Must(x => (x.Days?.Count ?? 0) > 0 || (x.MonthDates?.Count ?? 0) > 0)
            .WithName("Days").WithMessage("Choose at least one day.");
        RuleForEach(x => x.Days).IsInEnum();
        RuleForEach(x => x.MonthDates).InclusiveBetween(1, MonthDays.MaxDay);
        RuleFor(x => x.VisitStartTime)
            .Must(v => MonthlyServiceTimeFormat.TryParse(v, out _))
            .WithMessage("Visit time must be in HH:mm format.");
        RuleFor(x => x.EndDate)
            .GreaterThanOrEqualTo(x => x.StartDate)
            .When(x => x.EndDate.HasValue)
            .WithMessage("End date cannot be before the start date.");
        RuleFor(x => x.Note).MaximumLength(MonthlyServiceContract.MaxNoteLength);
    }
}

public class MonthlyServiceCancelRequestValidator : AbstractValidator<MonthlyServiceCancelRequest>
{
    public MonthlyServiceCancelRequestValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(MonthlyServiceContract.MaxNoteLength);
    }
}

public class MonthlyServiceDisputeRequestValidator : AbstractValidator<MonthlyServiceDisputeRequest>
{
    public MonthlyServiceDisputeRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(MonthlyServiceAttendance.MaxNoteLength);
    }
}

public class MonthlyServiceNoteRequestValidator : AbstractValidator<MonthlyServiceNoteRequest>
{
    public MonthlyServiceNoteRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(MonthlyServiceAttendance.MaxNoteLength);
    }
}

public class MonthlyServiceCheckInRequestValidator : AbstractValidator<MonthlyServiceCheckInRequest>
{
    public MonthlyServiceCheckInRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches("^[0-9]{4}$").WithMessage("Enter the 4-digit code.");
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90).When(x => x.Latitude.HasValue);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180).When(x => x.Longitude.HasValue);
    }
}

public class MonthlyServiceAssignProviderRequestValidator : AbstractValidator<MonthlyServiceAssignProviderRequest>
{
    public MonthlyServiceAssignProviderRequestValidator()
    {
        RuleFor(x => x.ProviderId).NotEmpty();
    }
}

public class MonthlyServiceResolveDisputeRequestValidator : AbstractValidator<MonthlyServiceResolveDisputeRequest>
{
    public MonthlyServiceResolveDisputeRequestValidator()
    {
        RuleFor(x => x.CorrectedStatus)
            .NotNull().WithMessage("Choose the status this visit should have.")
            .When(x => x.Upheld);
        RuleFor(x => x.CorrectedStatus).IsInEnum().When(x => x.CorrectedStatus.HasValue);
        RuleFor(x => x.Note).MaximumLength(MonthlyServiceAttendance.MaxNoteLength);
    }
}

public class MonthlyServiceCorrectAttendanceRequestValidator : AbstractValidator<MonthlyServiceCorrectAttendanceRequest>
{
    public MonthlyServiceCorrectAttendanceRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum().NotEqual(MonthlyServiceAttendanceStatus.Scheduled);
        RuleFor(x => x.Note).MaximumLength(MonthlyServiceAttendance.MaxNoteLength);
    }
}

public class MonthlyServiceRecordPaymentRequestValidator : AbstractValidator<MonthlyServiceRecordPaymentRequest>
{
    public MonthlyServiceRecordPaymentRequestValidator()
    {
        RuleFor(x => x.Method).IsInEnum().NotEqual(MonthlyServicePaymentMethod.Online)
            .WithMessage("Online payments are recorded by the payment flow, not by hand.");
        RuleFor(x => x.Reference).MaximumLength(MonthlyServiceInvoice.MaxReferenceLength);
    }
}

/// <summary>Query-string checks shared by the three APIs' attendance endpoints.</summary>
public static class MonthlyServiceQuery
{
    public const string InvalidMonthMessage = "Provide a valid year (2020-2100) and month (1-12).";

    public static bool IsValidMonth(int year, int month) => year is >= 2020 and <= 2100 && month is >= 1 and <= 12;
}
