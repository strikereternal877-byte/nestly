using Nestly.Application.Meals;
using Nestly.Domain.Meals;
using Nestly.Domain.MonthlyService;

namespace Nestly.Application.MonthlyService;

// ---- Plans ----

public sealed record MonthlyServicePlanAdminResponse(
    Guid Id,
    Guid ServiceId,
    string ServiceName,
    Guid CityId,
    string CityName,
    string Name,
    string? Description,
    MonthlyServicePlanBasis Basis,
    decimal? HoursPerVisit,
    IReadOnlyList<string> IncludedTasks,
    decimal RatePerVisit,
    decimal CommissionPercent,
    bool IsActive,
    int ActiveContractCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    MonthlyServiceFrequency Frequency,
    int? TimesPerPeriod,
    decimal MonthlyDiscountPercent = 0m);

/// <summary>Create and update share one shape: every field is editable.</summary>
public sealed record MonthlyServicePlanUpsertRequest(
    Guid ServiceId,
    Guid CityId,
    string Name,
    string? Description,
    MonthlyServicePlanBasis Basis,
    decimal? HoursPerVisit,
    IReadOnlyList<string>? IncludedTasks,
    decimal RatePerVisit,
    decimal CommissionPercent,
    MonthlyServiceFrequency Frequency = MonthlyServiceFrequency.Weekdays,
    int? TimesPerPeriod = null,
    /// <summary>MealBased plans only: monthly visits cost this much less than the same meals booked one-time (0-50 %).</summary>
    decimal MonthlyDiscountPercent = 0m);

/// <summary>Customer-facing subset of a plan - no commission.</summary>
public sealed record MonthlyServicePlanBrowseResponse(
    Guid Id,
    Guid ServiceId,
    string ServiceName,
    Guid CityId,
    string CityName,
    string Name,
    string? Description,
    MonthlyServicePlanBasis Basis,
    decimal? HoursPerVisit,
    IReadOnlyList<string> IncludedTasks,
    decimal RatePerVisit,
    MonthlyServiceFrequency Frequency,
    int? TimesPerPeriod,
    /// <summary>The service's slug, so a MealBased plan can send the customer to its meal picker.</summary>
    string? ServiceSlug = null,
    decimal MonthlyDiscountPercent = 0m);

// ---- Shared pieces ----

public sealed record MonthlyServiceAddressSummary(Guid Id, string Label, string Line1, string? Line2, string? Landmark, string City, string Pincode, string ContactName, string ContactMobile);

public sealed record MonthlyServiceProviderSummary(Guid Id, string DisplayName, string? PhotoUrl);

/// <summary>Attendance counts for a range - the numbers behind "this month so far" and the invoice preview.</summary>
public sealed record MonthlyServiceAttendanceSummary(
    int Present,
    int CustomerUnavailable,
    int CustomerSkipped,
    int ProviderLeave,
    int Absent,
    int Upcoming,
    int OpenDisputes,
    int BillableVisits,
    decimal BillableAmount);

/// <summary>One attendance row plus which actions the caller may take on it right now.</summary>
public sealed record MonthlyServiceAttendanceItem(
    Guid Id,
    Guid ContractId,
    DateOnly Date,
    string VisitStartTime,
    MonthlyServiceAttendanceStatus Status,
    MonthlyServiceAttendanceActor? MarkedBy,
    DateTime? MarkedAtUtc,
    string? Note,
    DateTime? CheckedInAtUtc,
    DateTime? CheckedOutAtUtc,
    MonthlyServiceDisputeStatus DisputeStatus,
    string? DisputeReason,
    string? DisputeResolutionNote,
    bool IsBillable,
    bool IsInvoiced,
    /// <summary>Customer only, and only on the day itself while still scheduled.</summary>
    string? DayCode,
    IReadOnlyList<string> AllowedActions,
    /// <summary>Daily wallet: what the wallet paid for this day; null when unpaid or refunded.</summary>
    decimal? PrepaidAmount = null);

public sealed record MonthlyServiceAttendanceMonthResponse(
    Guid ContractId,
    int Year,
    int Month,
    MonthlyServiceAttendanceSummary Summary,
    IReadOnlyList<MonthlyServiceAttendanceItem> Items);

// ---- Customer ----

public sealed record MonthlyServiceContractRequest(
    Guid PlanId,
    Guid AddressId,
    IReadOnlyList<DayOfWeek> Days,
    string VisitStartTime,
    DateOnly StartDate,
    DateOnly? EndDate,
    string? Note,
    /// <summary>Required for a "times per month" plan: exactly that many dates, 1-28. Ignored otherwise.</summary>
    IReadOnlyList<int>? MonthDates = null,
    /// <summary>Pay each month-end invoice from the wallet automatically.</summary>
    bool PayFromWallet = false,
    /// <summary>Month-end bill, or each day paid from the wallet before it happens.</summary>
    MonthlyServiceBillingMode BillingMode = MonthlyServiceBillingMode.MonthEnd);

/// <summary>
/// A cook on a MealBased plan (docs/MEAL-SERVICE.md). Morning meals and
/// evening meals become one contract each, so a request can create two;
/// <see cref="MorningVisitTime"/>/<see cref="EveningVisitTime"/> default to
/// the meals' own default times when omitted.
/// </summary>
public sealed record MonthlyServiceMealContractRequest(
    Guid PlanId,
    Guid AddressId,
    IReadOnlyList<DayOfWeek> Days,
    DateOnly StartDate,
    DateOnly? EndDate,
    string? Note,
    IReadOnlyList<MealType> Meals,
    int Persons,
    string? MorningVisitTime = null,
    string? EveningVisitTime = null,
    /// <summary>Pay each month-end invoice from the wallet automatically.</summary>
    bool PayFromWallet = false,
    /// <summary>Month-end bill, or each day paid from the wallet before it happens.</summary>
    MonthlyServiceBillingMode BillingMode = MonthlyServiceBillingMode.MonthEnd,
    /// <summary>The household's food preferences; remembered on the customer for next time.</summary>
    MealPreferencesDto? MealPreferences = null,
    /// <summary>"Keep this cook monthly": a professional who has cooked for this customer before.</summary>
    Guid? PreferredProviderId = null);

public sealed record MonthlyServiceContractResponse(
    Guid Id,
    Guid PlanId,
    string PlanName,
    string ServiceName,
    MonthlyServicePlanBasis Basis,
    decimal? HoursPerVisit,
    IReadOnlyList<string> IncludedTasks,
    decimal RatePerVisit,
    IReadOnlyList<DayOfWeek> Days,
    string VisitStartTime,
    DateOnly StartDate,
    DateOnly? EndDate,
    MonthlyServiceContractStatus Status,
    MonthlyServicePauseReason? PauseReason,
    string? CustomerNote,
    MonthlyServiceAddressSummary? Address,
    MonthlyServiceProviderSummary? Provider,
    MonthlyServiceAttendanceItem? Today,
    MonthlyServiceAttendanceSummary CurrentMonth,
    decimal UnpaidAmount,
    DateTime CreatedAtUtc,
    DateTime? CancelledAtUtc,
    string? CancellationReason,
    MonthlyServiceFrequency Frequency,
    int? TimesPerPeriod,
    IReadOnlyList<int> MonthDates,
    /// <summary>The next day a visit is still due (today included), skipping days already skipped or on leave; null unless active.</summary>
    DateOnly? NextVisitDate = null,
    /// <summary>Meal plans only: how many people the cook cooks for (the meals are <see cref="IncludedTasks"/>).</summary>
    int? Persons = null,
    /// <summary>Whether month-end invoices are paid from the wallet automatically.</summary>
    bool PayFromWallet = false,
    MonthlyServiceBillingMode BillingMode = MonthlyServiceBillingMode.MonthEnd,
    MealPreferencesDto? MealPreferences = null,
    /// <summary>The cook the customer asked for, until one is assigned.</summary>
    MonthlyServiceProviderSummary? PreferredProvider = null);

public sealed record MonthlyServiceCancelRequest(string? Reason);

/// <summary>Turns automatic wallet payment of month-end invoices on or off.</summary>
public sealed record MonthlyServiceWalletAutopayRequest(bool Enabled);

public sealed record MonthlyServiceDisputeRequest(string Reason);

public sealed record MonthlyServiceNoteRequest(string? Note);

public sealed record MonthlyServiceInvoiceResponse(
    Guid Id,
    Guid ContractId,
    string PlanName,
    Guid CustomerId,
    string CustomerName,
    Guid ProviderId,
    string ProviderName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int PresentCount,
    int CustomerUnavailableCount,
    int CustomerSkippedCount,
    int ProviderLeaveCount,
    int AbsentCount,
    int BillableVisits,
    decimal RatePerVisit,
    decimal Amount,
    /// <summary>Admin and professional views only; zero in the customer view.</summary>
    decimal CommissionAmount,
    decimal ProviderNetAmount,
    MonthlyServiceInvoiceStatus Status,
    DateTime IssuedAtUtc,
    DateOnly DueDate,
    DateTime? PaidAtUtc,
    MonthlyServicePaymentMethod? PaymentMethod,
    string? PaymentReference);

// ---- Professional ----

public sealed record MonthlyServiceProviderContractResponse(
    Guid Id,
    string PlanName,
    string ServiceName,
    MonthlyServicePlanBasis Basis,
    decimal? HoursPerVisit,
    IReadOnlyList<string> IncludedTasks,
    IReadOnlyList<DayOfWeek> Days,
    string VisitStartTime,
    DateOnly StartDate,
    DateOnly? EndDate,
    MonthlyServiceContractStatus Status,
    string CustomerName,
    string? CustomerNote,
    MonthlyServiceAddressSummary? Address,
    decimal RatePerVisit,
    decimal NetPerVisit,
    MonthlyServiceAttendanceSummary CurrentMonth,
    MonthlyServiceFrequency Frequency,
    int? TimesPerPeriod,
    IReadOnlyList<int> MonthDates,
    int? Persons = null,
    MealPreferencesDto? MealPreferences = null);

/// <summary>One visit on the professional's day list: the attendance row plus where and what.</summary>
public sealed record MonthlyServiceProviderVisitResponse(
    MonthlyServiceAttendanceItem Attendance,
    string PlanName,
    MonthlyServicePlanBasis Basis,
    decimal? HoursPerVisit,
    IReadOnlyList<string> IncludedTasks,
    string CustomerName,
    MonthlyServiceAddressSummary? Address,
    int? Persons = null,
    MealPreferencesDto? MealPreferences = null);

public sealed record MonthlyServiceCheckInRequest(string Code, decimal? Latitude, decimal? Longitude);

// ---- Admin ----

public sealed record MonthlyServiceContractAdminListItem(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    string CustomerPhone,
    string PlanName,
    string CityName,
    MonthlyServiceContractStatus Status,
    MonthlyServicePauseReason? PauseReason,
    Guid? ProviderId,
    string? ProviderName,
    IReadOnlyList<DayOfWeek> Days,
    string VisitStartTime,
    DateOnly StartDate,
    DateOnly? EndDate,
    DateTime CreatedAtUtc,
    MonthlyServiceFrequency Frequency,
    int? TimesPerPeriod,
    IReadOnlyList<int> MonthDates);

public sealed record MonthlyServiceContractAdminSearchResponse(
    IReadOnlyList<MonthlyServiceContractAdminListItem> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record MonthlyServiceContractAdminDetailResponse(
    MonthlyServiceContractAdminListItem Contract,
    MonthlyServicePlanBasis Basis,
    decimal? HoursPerVisit,
    IReadOnlyList<string> IncludedTasks,
    decimal RatePerVisit,
    decimal CommissionPercent,
    string? CustomerNote,
    MonthlyServiceAddressSummary? Address,
    MonthlyServiceAttendanceSummary CurrentMonth,
    IReadOnlyList<MonthlyServiceInvoiceResponse> Invoices,
    DateTime? CancelledAtUtc,
    string? CancellationReason,
    int? Persons = null,
    MonthlyServiceBillingMode BillingMode = MonthlyServiceBillingMode.MonthEnd,
    MealPreferencesDto? MealPreferences = null,
    /// <summary>The cook the customer asked for ("keep this cook monthly"); offer them first.</summary>
    MonthlyServiceProviderSummary? PreferredProvider = null);

public sealed record MonthlyServiceEligibleProvider(
    Guid Id,
    string DisplayName,
    string Phone,
    bool HasSkill,
    bool ServesCity,
    int ActiveContractCount,
    /// <summary>Non-null when this professional already has a contract overlapping this one's days and time.</summary>
    string? Conflict);

public sealed record MonthlyServiceAssignProviderRequest(Guid ProviderId);

public sealed record MonthlyServiceResolveDisputeRequest(bool Upheld, MonthlyServiceAttendanceStatus? CorrectedStatus, string? Note);

public sealed record MonthlyServiceCorrectAttendanceRequest(MonthlyServiceAttendanceStatus Status, string? Note);

public sealed record MonthlyServiceDisputeAdminItem(
    MonthlyServiceAttendanceItem Attendance,
    string CustomerName,
    string ProviderName,
    string PlanName);

public sealed record MonthlyServiceInvoiceAdminSearchResponse(
    IReadOnlyList<MonthlyServiceInvoiceResponse> Items,
    int TotalCount,
    int Page,
    int PageSize,
    decimal OutstandingAmount);

public sealed record MonthlyServiceRecordPaymentRequest(MonthlyServicePaymentMethod Method, string? Reference);

/// <summary>Result of one run of the daily job - logged, and returned by the admin "run now" endpoint.</summary>
public sealed record MonthlyServiceDailyRunResult(
    int DaysClosedAsAbsent,
    int AttendanceRowsScheduled,
    int InvoicesIssued,
    int InvoicesHeldForDisputes,
    int InvoicesMarkedOverdue,
    int ContractsPausedForNonPayment,
    int InvoicesPaidFromWallet = 0,
    int VisitsPaidFromWallet = 0,
    int VisitsNotBookedLowBalance = 0,
    int LowBalanceWarnings = 0);
