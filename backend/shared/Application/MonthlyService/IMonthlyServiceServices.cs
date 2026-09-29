using Nestly.BuildingBlocks.Results;
using Nestly.Domain.MonthlyService;

namespace Nestly.Application.MonthlyService;

/// <summary>Customer side of docs/MONTHLY-SERVICE.md. Every action is scoped to the caller's own customer id.</summary>
public interface IMonthlyServiceCustomerService
{
    Task<IReadOnlyList<MonthlyServicePlanBrowseResponse>> BrowsePlansAsync(Guid? cityId);

    Task<Result<MonthlyServiceContractResponse>> RequestContractAsync(Guid customerId, MonthlyServiceContractRequest request);

    Task<IReadOnlyList<MonthlyServiceContractResponse>> ListContractsAsync(Guid customerId);

    Task<Result<MonthlyServiceContractResponse>> GetContractAsync(Guid customerId, Guid contractId);

    Task<Result<MonthlyServiceAttendanceMonthResponse>> GetAttendanceAsync(Guid customerId, Guid contractId, int year, int month);

    Task<Result> CancelContractAsync(Guid customerId, Guid contractId, string? reason);

    Task<Result<MonthlyServiceAttendanceItem>> SkipAsync(Guid customerId, Guid attendanceId);

    Task<Result<MonthlyServiceAttendanceItem>> UnskipAsync(Guid customerId, Guid attendanceId);

    Task<Result<MonthlyServiceAttendanceItem>> ConfirmVisitAsync(Guid customerId, Guid attendanceId);

    Task<Result<MonthlyServiceAttendanceItem>> DisputeAsync(Guid customerId, Guid attendanceId, string reason);

    Task<IReadOnlyList<MonthlyServiceInvoiceResponse>> ListInvoicesAsync(Guid customerId);

    Task<Result<MonthlyServiceInvoiceResponse>> PayInvoiceAsync(Guid customerId, Guid invoiceId);
}

/// <summary>Professional side: their engagements, their day, and attendance marking.</summary>
public interface IMonthlyServiceProviderService
{
    Task<IReadOnlyList<MonthlyServiceProviderContractResponse>> ListContractsAsync(Guid providerId);

    Task<Result<MonthlyServiceAttendanceMonthResponse>> GetAttendanceAsync(Guid providerId, Guid contractId, int year, int month);

    Task<IReadOnlyList<MonthlyServiceProviderVisitResponse>> ListVisitsAsync(Guid providerId, DateOnly? date);

    Task<IReadOnlyList<MonthlyServiceInvoiceResponse>> ListInvoicesAsync(Guid providerId);

    Task<Result<MonthlyServiceAttendanceItem>> CheckInAsync(Guid providerId, Guid attendanceId, MonthlyServiceCheckInRequest request);

    Task<Result<MonthlyServiceAttendanceItem>> CheckOutAsync(Guid providerId, Guid attendanceId);

    Task<Result<MonthlyServiceAttendanceItem>> MarkCustomerUnavailableAsync(Guid providerId, Guid attendanceId, string? note);

    Task<Result<MonthlyServiceAttendanceItem>> MarkLeaveAsync(Guid providerId, Guid attendanceId, string? note);

    Task<Result<MonthlyServiceAttendanceItem>> CancelLeaveAsync(Guid providerId, Guid attendanceId);
}

public interface IMonthlyServiceAdminService
{
    Task<IReadOnlyList<MonthlyServicePlanAdminResponse>> ListPlansAsync();

    Task<Result<MonthlyServicePlanAdminResponse>> GetPlanAsync(Guid id);

    Task<Result<MonthlyServicePlanAdminResponse>> CreatePlanAsync(MonthlyServicePlanUpsertRequest request, Guid adminUserId);

    Task<Result<MonthlyServicePlanAdminResponse>> UpdatePlanAsync(Guid id, MonthlyServicePlanUpsertRequest request, Guid adminUserId);

    Task<Result> SetPlanActiveAsync(Guid id, bool active, Guid adminUserId);

    Task<MonthlyServiceContractAdminSearchResponse> SearchContractsAsync(MonthlyServiceContractStatus? status, string? customerSearch, int page, int pageSize);

    Task<Result<MonthlyServiceContractAdminDetailResponse>> GetContractAsync(Guid id);

    Task<Result<MonthlyServiceAttendanceMonthResponse>> GetAttendanceAsync(Guid contractId, int year, int month);

    Task<Result<IReadOnlyList<MonthlyServiceEligibleProvider>>> ListEligibleProvidersAsync(Guid contractId);

    Task<Result<MonthlyServiceContractAdminDetailResponse>> AssignProviderAsync(Guid contractId, Guid providerId, Guid adminUserId);

    Task<Result> PauseContractAsync(Guid contractId, Guid adminUserId);

    Task<Result> ResumeContractAsync(Guid contractId, Guid adminUserId);

    Task<Result> CancelContractAsync(Guid contractId, string? reason, Guid adminUserId);

    Task<IReadOnlyList<MonthlyServiceDisputeAdminItem>> ListOpenDisputesAsync();

    Task<Result<MonthlyServiceAttendanceItem>> ResolveDisputeAsync(Guid attendanceId, MonthlyServiceResolveDisputeRequest request, Guid adminUserId);

    Task<Result<MonthlyServiceAttendanceItem>> CorrectAttendanceAsync(Guid attendanceId, MonthlyServiceCorrectAttendanceRequest request, Guid adminUserId);

    Task<MonthlyServiceInvoiceAdminSearchResponse> SearchInvoicesAsync(MonthlyServiceInvoiceStatus? status, int page, int pageSize);

    Task<Result<MonthlyServiceInvoiceResponse>> RecordPaymentAsync(Guid invoiceId, MonthlyServiceRecordPaymentRequest request, Guid adminUserId);

    Task<MonthlyServiceDailyRunResult> RunDailyJobNowAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The once-a-day sweep (docs/MONTHLY-SERVICE.md HOW IT WORKS): close past
/// days, schedule ahead, issue month-end invoices, and act on overdue ones.
/// Every step is idempotent, so a missed or repeated run is harmless.
/// </summary>
public interface IMonthlyServiceDailyJob
{
    Task<MonthlyServiceDailyRunResult> RunAsync(CancellationToken cancellationToken);
}
