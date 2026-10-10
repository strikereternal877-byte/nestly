using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nestly.Application;
using Nestly.Application.Abstractions.Auditing;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IAdminWalletTopUpService"/>
public sealed class AdminWalletTopUpService : IAdminWalletTopUpService
{
    private static readonly TimeSpan Last24Hours = TimeSpan.FromHours(24);

    private readonly NestlyDbContext _context;
    private readonly IWalletTopUpService _topUpService;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly TimeProvider _timeProvider;

    public AdminWalletTopUpService(
        NestlyDbContext context, IWalletTopUpService topUpService, IAuditLogWriter auditLogWriter, TimeProvider timeProvider)
    {
        _context = context;
        _topUpService = topUpService;
        _auditLogWriter = auditLogWriter;
        _timeProvider = timeProvider;
    }

    public async Task<Result<PagedAdminWalletTopUpResponse>> SearchAsync(AdminWalletTopUpFilterRequest filter)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var stuckBefore = now.AddMinutes(-IAdminWalletTopUpService.StuckAfterMinutes);

        var topUps = _context.Set<WalletTopUp>().AsNoTracking();

        // The summary describes the whole table, so it is computed before any filter narrows it.
        int pendingCount = await topUps.CountAsync(t => t.Status == WalletTopUpStatus.Pending);
        int stuckCount = await topUps.CountAsync(t => t.Status == WalletTopUpStatus.Pending && t.CreatedAtUtc <= stuckBefore);
        int needsReviewCount = await topUps.CountAsync(t => t.ReviewReason != null);

        var creditedSince = now - Last24Hours;
        // Summed in memory: the set is small (one day of top-ups) and SQLite, which the tests use, cannot SUM decimals.
        var creditedAmounts = await topUps
            .Where(t => t.Status == WalletTopUpStatus.Success && t.CompletedAtUtc >= creditedSince)
            .Select(t => t.Amount)
            .ToListAsync();

        IQueryable<WalletTopUp> filtered = topUps;

        if (filter.Status is { } status)
        {
            filtered = filtered.Where(t => t.Status == status);
        }

        if (filter.FromUtc is { } from)
        {
            filtered = filtered.Where(t => t.CreatedAtUtc >= from);
        }

        if (filter.ToUtc is { } to)
        {
            filtered = filtered.Where(t => t.CreatedAtUtc <= to);
        }

        if (filter.NeedsAttention == true)
        {
            filtered = filtered.Where(t => t.ReviewReason != null
                || (t.Status == WalletTopUpStatus.Pending && t.CreatedAtUtc <= stuckBefore));
        }

        var rows =
            from topUp in filtered
            join customer in _context.Set<Customer>().AsNoTracking() on topUp.CustomerId equals customer.Id
            select new { TopUp = topUp, CustomerName = customer.Name, CustomerMobile = customer.Mobile };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            string needle = filter.Search.Trim();
            string lowered = needle.ToLowerInvariant();
            rows = rows.Where(r => r.CustomerName.ToLower().Contains(lowered)
                || r.CustomerMobile.Contains(needle)
                || r.TopUp.GatewayOrderId.Contains(needle));
        }

        int totalCount = await rows.CountAsync();

        (int page, int pageSize) = PagedQueryExtensions.Normalize(filter.Page, filter.PageSize);

        // Id breaks ties on CreatedAtUtc so two top-ups created in the same tick cannot swap places between pages.
        var pageRows = await rows
            .OrderByDescending(r => r.TopUp.CreatedAtUtc)
            .ThenBy(r => r.TopUp.Id)
            .ApplyPaging(page, pageSize)
            .ToListAsync();

        var items = pageRows.Select(r => ToResponse(r.TopUp, r.CustomerName, r.CustomerMobile, now)).ToList();

        return new PagedAdminWalletTopUpResponse(
            items, totalCount, page, pageSize, pendingCount, stuckCount, needsReviewCount,
            creditedAmounts.Count, creditedAmounts.Sum());
    }

    public async Task<Result<AdminWalletTopUpResponse>> GetAsync(Guid topUpId)
    {
        var item = await LoadAsync(topUpId);
        return item is null ? NotFound : item;
    }

    public async Task<Result<AdminWalletTopUpReconcileResponse>> ReconcileAsync(Guid topUpId, Guid adminUserId)
    {
        var before = await LoadAsync(topUpId);
        if (before is null)
        {
            return NotFound;
        }

        var outcome = await _topUpService.ReconcileNowAsync(topUpId);
        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        var after = await LoadAsync(topUpId) ?? before;

        await _auditLogWriter.WriteAsync(new AuditEntry(
            "WalletTopUp", topUpId.ToString(), "AdminReconcile",
            JsonSerializer.Serialize(new { before.Status }),
            JsonSerializer.Serialize(new { after.Status, Outcome = outcome.Value, AdminUserId = adminUserId })));
        await _context.SaveChangesAsync();

        return new AdminWalletTopUpReconcileResponse(outcome.Value, after);
    }

    private static Error NotFound => Error.NotFound("WalletTopUp.NotFound", "That top-up does not exist.");

    private async Task<AdminWalletTopUpResponse?> LoadAsync(Guid topUpId)
    {
        var row = await (
            from topUp in _context.Set<WalletTopUp>().AsNoTracking()
            join customer in _context.Set<Customer>().AsNoTracking() on topUp.CustomerId equals customer.Id
            where topUp.Id == topUpId
            select new { TopUp = topUp, CustomerName = customer.Name, CustomerMobile = customer.Mobile })
            .FirstOrDefaultAsync();

        return row is null
            ? null
            : ToResponse(row.TopUp, row.CustomerName, row.CustomerMobile, _timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>The attention flag is derived, not stored (apart from the review reason): a Pending row becomes "stuck" purely by getting old.</summary>
    internal static AdminWalletTopUpResponse ToResponse(WalletTopUp topUp, string customerName, string customerMobile, DateTime now)
    {
        int? ageMinutes = topUp.Status == WalletTopUpStatus.Pending
            ? Math.Max(0, (int)(now - topUp.CreatedAtUtc).TotalMinutes)
            : null;

        var (attention, reason) = Assess(topUp, ageMinutes);

        return new AdminWalletTopUpResponse(
            topUp.Id, topUp.CustomerId, customerName, customerMobile,
            topUp.Amount, topUp.Currency, topUp.Status,
            topUp.GatewayOrderId, topUp.GatewayPaymentRef, topUp.FailureReason, topUp.WalletLedgerEntryId,
            topUp.CreatedAtUtc, topUp.CompletedAtUtc, ageMinutes, attention, reason);
    }

    private static (AdminWalletTopUpAttention Attention, string? Reason) Assess(WalletTopUp topUp, int? ageMinutes)
    {
        if (topUp.NeedsReview)
        {
            return (AdminWalletTopUpAttention.NeedsReview, topUp.ReviewReason);
        }

        if (ageMinutes is { } age && age >= IAdminWalletTopUpService.StuckAfterMinutes)
        {
            return (
                AdminWalletTopUpAttention.Stuck,
                $"Still pending after {age} minutes - the gateway has not given a final answer. Use Reconcile now, or check the gateway dashboard.");
        }

        return (AdminWalletTopUpAttention.None, null);
    }
}
