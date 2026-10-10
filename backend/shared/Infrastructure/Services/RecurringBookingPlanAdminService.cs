using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Abstractions.Time;
using Nestly.Application.Bookings;
using Nestly.Application.RecurringBookings;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// <inheritdoc cref="IRecurringBookingPlanAdminService"/>
///
/// Reads <see cref="NestlyDbContext"/> directly rather than through
/// <c>IRecurringBookingPlanRepository</c>, for the reason
/// <c>ReportingQueryService</c> and <c>DashboardQueryService</c> already
/// document: these are read-only cross-aggregate queries with no write side,
/// and the plan repository's methods all load a plan with its add-ons for
/// mutation, which is the opposite of what a count needs.
///
/// Every aggregate below is a <c>GroupBy</c>/<c>CountAsync</c> that runs in
/// the database. None of them materializes plan or booking rows - a platform
/// with 50,000 standing plans must not stream 50,000 rows into the API
/// process to answer "how many are paused". The only work done in memory is
/// zero-filling status/frequency buckets the database had no rows for and
/// adding up the per-day counts, both of which operate on a handful of
/// already-aggregated rows rather than on the underlying tables.
/// </summary>
public sealed class RecurringBookingPlanAdminService : IRecurringBookingPlanAdminService
{
    /// <summary>
    /// Statuses that mean the visit is off, excluded from
    /// <c>UpcomingOccurrenceVolume</c>: counting them would tell an admin to
    /// staff for work nobody is going to do. Deliberately not
    /// <c>BookingLifecycle.IsTerminal</c> - <see cref="BookingStatus.Completed"/>
    /// is terminal too, and a completed booking inside the horizon is work
    /// that did happen, so it still belongs in the volume.
    /// </summary>
    private static readonly BookingStatus[] CalledOffStatuses =
        [BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin, BookingStatus.Expired];

    /// <summary>How many upcoming and how many past visits the plan detail lists - enough to see the pattern without paging.</summary>
    private const int VisitsPerDirection = 10;

    private readonly NestlyDbContext _context;
    private readonly IRecurringBookingPlanRepository _planRepository;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly IRecurringPlanNotifier _notifier;
    private readonly IBusinessClock _clock;
    private readonly ILogger<RecurringBookingPlanAdminService> _logger;

    public RecurringBookingPlanAdminService(
        NestlyDbContext context,
        IRecurringBookingPlanRepository planRepository,
        IAuditLogWriter auditLogWriter,
        IRecurringPlanNotifier notifier,
        IBusinessClock clock,
        ILogger<RecurringBookingPlanAdminService> logger)
    {
        _context = context;
        _planRepository = planRepository;
        _auditLogWriter = auditLogWriter;
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<AdminRecurringPlanSearchResponse>> SearchAsync(AdminRecurringPlanSearchRequest request)
    {
        IQueryable<RecurringBookingPlan> plans = _context.RecurringBookingPlans.AsNoTracking();


        if (request.Status is { } status)
        {
            plans = plans.Where(p => p.Status == status);
        }

        if (request.Frequency is { } frequency)
        {
            plans = plans.Where(p => p.Frequency == frequency);
        }

        if (request.CustomerId is { } customerId)
        {
            plans = plans.Where(p => p.CustomerId == customerId);
        }

        if (request.ServiceId is { } serviceId)
        {
            plans = plans.Where(p => p.ServiceId == serviceId);
        }

        if (request.PauseReason is { } pauseReason)
        {
            plans = plans.Where(p => p.PauseReason == pauseReason);
        }

        if (request.PrepaidUpfront is { } prepaid)
        {
            plans = plans.Where(p => p.PrepaidUpfront == prepaid);
        }

        int totalCount = await plans.CountAsync();

        // Inner joins are safe here: RecurringBookingPlanConfiguration puts a
        // Restrict foreign key on both CustomerId and ServiceId, so neither
        // referenced row can be deleted out from under a plan.
        var rows =
            from plan in plans
            join customer in _context.Set<Customer>().AsNoTracking() on plan.CustomerId equals customer.Id
            join service in _context.Set<Service>().AsNoTracking() on plan.ServiceId equals service.Id
            select new { Plan = plan, CustomerName = customer.Name, ServiceName = service.Name };

        (int page, int pageSize) = PagedQueryExtensions.Normalize(request.Page, request.PageSize);

        // Ordered and paged on the joined columns, then projected - ordering by
        // a member of the final record instead does not translate, because the
        // projection is a constructor call the provider cannot see through.
        // Id breaks ties on CreatedAtUtc so two plans created in the same tick
        // cannot swap places between page 1 and page 2 (PagedQueryExtensions:
        // an unstably ordered paged query has no stable page boundaries).
        var items = await rows
            .OrderByDescending(r => r.Plan.CreatedAtUtc)
            .ThenBy(r => r.Plan.Id)
            .ApplyPaging(page, pageSize)
            .Select(r => new AdminRecurringPlanSummaryResponse(
                r.Plan.Id,
                r.Plan.CustomerId,
                r.CustomerName,
                r.Plan.ServiceId,
                r.ServiceName,
                r.Plan.Frequency,
                r.Plan.RecurrenceDayOfWeek,
                r.Plan.RecurrenceDayOfMonth,
                r.Plan.StartDate,
                r.Plan.EndDate,
                r.Plan.OccurrenceCount,
                r.Plan.CompletedOccurrenceCount,
                r.Plan.NextOccurrenceDate,
                r.Plan.Status,
                r.Plan.CreatedAtUtc,
                r.Plan.PrepaidUpfront,
                r.Plan.AutoChargeEnabled,
                r.Plan.ApplyWalletCredit,
                r.Plan.PendingPrepaymentLeadBookingId != null,
                r.Plan.PrepaidThroughDate,
                r.Plan.PauseReason,
                r.Plan.SkipUntilDate))
            .ToListAsync();

        return new AdminRecurringPlanSearchResponse(items, totalCount, page, pageSize);
    }

    public async Task<Result<AdminRecurringPlanReportResponse>> GetReportAsync(AdminRecurringPlanReportRequest request)
    {
        var fromDate = request.FromDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var toDate = request.ToDate ?? fromDate.AddDays(IRecurringBookingPlanAdminService.DefaultHorizonDays);

        if (toDate < fromDate)
        {
            return Error.Validation("Reports.InvalidDateRange", "The 'to' date cannot be before the 'from' date.");
        }

        var byStatus = await _context.RecurringBookingPlans
            .GroupBy(p => p.Status)
            .Select(g => new RecurringPlanStatusCountRow(g.Key, g.Count()))
            .ToListAsync();

        var activeByFrequency = await _context.RecurringBookingPlans
            .Where(p => p.Status == RecurringBookingPlanStatus.Active)
            .GroupBy(p => p.Frequency)
            .Select(g => new RecurringPlanFrequencyCountRow(g.Key, g.Count()))
            .ToListAsync();

        int plansDueInHorizon = await _context.RecurringBookingPlans
            .CountAsync(p => p.Status == RecurringBookingPlanStatus.Active
                && p.NextOccurrenceDate >= fromDate
                && p.NextOccurrenceDate <= toDate);

        // Task 296's forward link is what makes this answerable without a join
        // to recurring_booking_occurrence: a booking generated by a plan
        // carries the plan's id, so "recurring work in this window" is a
        // predicate on the booking table alone.
        var volumeByDate = await _context.Bookings
            .Where(b => b.RecurringBookingPlanId != null
                && b.SlotDate >= fromDate
                && b.SlotDate <= toDate
                && !CalledOffStatuses.Contains(b.Status))
            .GroupBy(b => b.SlotDate)
            // Ordered by the grouping key, not by the projected record's
            // property: the projection is a constructor call the provider
            // cannot see through, so ordering after it falls back to client
            // evaluation - which EF refuses outright.
            .OrderBy(g => g.Key)
            .Select(g => new RecurringPlanDailyVolumeRow(g.Key, g.Count()))
            .ToListAsync();

        return new AdminRecurringPlanReportResponse(
            byStatus.Sum(r => r.PlanCount),
            ZeroFill(byStatus, Enum.GetValues<RecurringBookingPlanStatus>(), r => r.Status, s => new RecurringPlanStatusCountRow(s, 0)),
            ZeroFill(activeByFrequency, Enum.GetValues<RecurringBookingRecurrenceFrequency>(), r => r.Frequency, f => new RecurringPlanFrequencyCountRow(f, 0)),
            fromDate,
            toDate,
            plansDueInHorizon,
            volumeByDate.Sum(r => r.BookingCount),
            volumeByDate);
    }

    public async Task<Result<AdminRecurringPlanDetailResponse>> GetAsync(Guid planId)
    {
        var plan = await _context.RecurringBookingPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId);
        if (plan is null)
        {
            return PlanNotFound;
        }

        var customer = await _context.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == plan.CustomerId);
        var summary = await ToSummaryAsync(plan, customer?.Name);

        decimal balance = await _context.WalletLedgerEntries.AsNoTracking()
            .Where(e => e.CustomerId == plan.CustomerId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .Select(e => (decimal?)e.BalanceAfter)
            .FirstOrDefaultAsync() ?? 0m;

        var today = _clock.Today;
        var planVisits = _context.Bookings.AsNoTracking().Where(b => b.RecurringBookingPlanId == planId);

        var upcomingRows = await planVisits
            .Where(b => b.SlotDate >= today)
            .OrderBy(b => b.SlotDate).ThenBy(b => b.Id)
            .Take(VisitsPerDirection)
            .Select(b => new { b.Id, b.BookingReference, b.SlotDate, b.Status, b.TotalPayableSnapshot })
            .ToListAsync();

        var pastRows = await planVisits
            .Where(b => b.SlotDate < today)
            .OrderByDescending(b => b.SlotDate).ThenBy(b => b.Id)
            .Take(VisitsPerDirection)
            .Select(b => new { b.Id, b.BookingReference, b.SlotDate, b.Status, b.TotalPayableSnapshot })
            .ToListAsync();

        var visits = upcomingRows.Concat(pastRows)
            .Select(b => new AdminRecurringPlanVisitResponse(
                b.Id, b.BookingReference, b.SlotDate, b.Status, BookingStatusMapper.LabelFor(b.Status), b.TotalPayableSnapshot))
            .ToList();

        return new AdminRecurringPlanDetailResponse(summary, customer?.Mobile ?? string.Empty, balance, visits);
    }

    public async Task<Result<AdminRecurringPlanSummaryResponse>> CancelAsync(Guid planId, Guid adminUserId, AdminCancelRecurringPlanRequest request)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan is null)
        {
            return PlanNotFound;
        }

        var previousStatus = plan.Status;
        try
        {
            plan.Cancel();
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("RecurringBookingPlan.InvalidCancel", ex.Message);
        }

        await _planRepository.UpdateAsync(plan);

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "RecurringBookingPlan", planId.ToString(), "AdminCancel",
            JsonSerializer.Serialize(new { Status = previousStatus }),
            JsonSerializer.Serialize(new { Status = plan.Status, request.Reason })));
        await _context.SaveChangesAsync();

        await TellCustomerAsync(plan, RecurringPlanChangeKind.CancelledBySupport);
        return await ToSummaryAsync(plan);
    }

    public async Task<Result<AdminRecurringPlanSummaryResponse>> PauseAsync(Guid planId, Guid adminUserId, AdminPauseRecurringPlanRequest request)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan is null)
        {
            return PlanNotFound;
        }

        var previousStatus = plan.Status;
        try
        {
            plan.PauseByAdmin();
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("RecurringBookingPlan.InvalidPause", ex.Message);
        }

        await _planRepository.UpdateAsync(plan);

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "RecurringBookingPlan", planId.ToString(), "AdminPause",
            JsonSerializer.Serialize(new { Status = previousStatus }),
            JsonSerializer.Serialize(new { Status = plan.Status, PauseReason = plan.PauseReason, request.Reason, AdminUserId = adminUserId })));
        await _context.SaveChangesAsync();

        await TellCustomerAsync(plan, RecurringPlanChangeKind.PausedBySupport);
        return await ToSummaryAsync(plan);
    }

    public async Task<Result<AdminRecurringPlanSummaryResponse>> ResumeAsync(Guid planId, Guid adminUserId, AdminResumeRecurringPlanRequest request)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan is null)
        {
            return PlanNotFound;
        }

        var previousStatus = plan.Status;
        var previousReason = plan.PauseReason;
        try
        {
            // Today is passed for the same reason the customer's resume passes it: a cursor that fell behind while the
            // plan sat paused must not make the scheduler "skip" and notify for every date that has already gone by.
            plan.Resume(_clock.Today);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("RecurringBookingPlan.InvalidResume", ex.Message);
        }

        await _planRepository.UpdateAsync(plan);

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "RecurringBookingPlan", planId.ToString(), "AdminResume",
            JsonSerializer.Serialize(new { Status = previousStatus, PauseReason = previousReason }),
            JsonSerializer.Serialize(new { Status = plan.Status, request.Reason, AdminUserId = adminUserId })));
        await _context.SaveChangesAsync();

        await TellCustomerAsync(plan, RecurringPlanChangeKind.ResumedBySupport);
        return await ToSummaryAsync(plan);
    }

    private static Error PlanNotFound { get; } =
        Error.NotFound("RecurringBookingPlan.NotFound", "The specified recurring booking plan does not exist.");

    private async Task<AdminRecurringPlanSummaryResponse> ToSummaryAsync(RecurringBookingPlan plan, string? knownCustomerName = null)
    {
        string customerName = knownCustomerName
            ?? (await _context.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == plan.CustomerId))?.Name
            ?? string.Empty;
        string serviceName = (await _context.Set<Service>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == plan.ServiceId))?.Name ?? string.Empty;

        return new AdminRecurringPlanSummaryResponse(
            plan.Id, plan.CustomerId, customerName, plan.ServiceId, serviceName,
            plan.Frequency, plan.RecurrenceDayOfWeek, plan.RecurrenceDayOfMonth, plan.StartDate, plan.EndDate,
            plan.OccurrenceCount, plan.CompletedOccurrenceCount, plan.NextOccurrenceDate, plan.Status, plan.CreatedAtUtc,
            plan.PrepaidUpfront, plan.AutoChargeEnabled, plan.ApplyWalletCredit, plan.IsAwaitingPrepayment,
            plan.PrepaidThroughDate, plan.PauseReason, plan.SkipUntilDate);
    }

    /// <summary>
    /// Tells the customer what support just did to their plan. Runs only after the change has been saved and can never
    /// fail it: the visits-still-ahead facts and the sending are both best effort, logged if they go wrong.
    /// </summary>
    private async Task TellCustomerAsync(RecurringBookingPlan plan, RecurringPlanChangeKind kind)
    {
        try
        {
            var today = _clock.Today;
            var upcoming = await _context.Bookings.AsNoTracking()
                .Where(b => b.RecurringBookingPlanId == plan.Id && b.SlotDate >= today)
                .Select(b => new { b.SlotDate, b.Status })
                .ToListAsync();

            // Only visits that can still be called off are "still ahead and still charged": a finished or already
            // cancelled one is not something the customer has to do anything about.
            var ahead = upcoming
                .Where(b => BookingLifecycle.IsValidTransition(b.Status, BookingStatus.CancelledByCustomer))
                .OrderBy(b => b.SlotDate)
                .ToList();

            await _notifier.NotifyChangedAsync(plan, new RecurringPlanChange(
                kind,
                BookedVisitsStillAhead: ahead.Count,
                NextBookedVisitDate: ahead.Count > 0 ? ahead[0].SlotDate : null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not tell the customer about the {Kind} change to recurring plan {PlanId}.", kind, plan.Id);
        }
    }

    /// <summary>
    /// Adds a zero row for every enum member the grouped query returned
    /// nothing for, in declaration order. A report that silently omits
    /// "Cancelled: 0" reads as "cancellations were not measured" rather than
    /// "there were none", and a chart built off it changes shape the first
    /// time a bucket empties.
    /// </summary>
    private static IReadOnlyList<TRow> ZeroFill<TRow, TKey>(
        IReadOnlyList<TRow> rows,
        TKey[] allKeys,
        Func<TRow, TKey> keyOf,
        Func<TKey, TRow> emptyRow)
        where TKey : struct
    {
        var byKey = rows.ToDictionary(keyOf);
        return allKeys.Select(key => byKey.TryGetValue(key, out var row) ? row : emptyRow(key)).ToList();
    }
}
