using Microsoft.EntityFrameworkCore;
using Nestly.Application.ProviderSupport;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Repositories;

public class ProviderSupportTicketRepository : IProviderSupportTicketRepository
{
    private readonly NestlyDbContext _context;

    public ProviderSupportTicketRepository(NestlyDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(ProviderSupportTicket ticket)
    {
        await _context.ProviderSupportTickets.AddAsync(ticket);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(ProviderSupportTicket ticket)
    {
        // Same guard as SupportTicketRepository.UpdateAsync: only attach+mark-
        // modified when not already tracked by this context - a same-context
        // AddComment() call appends a brand-new ProviderSupportTicketComment
        // row, corrected centrally by NewOwnedChildEntityInterceptor.
        if (_context.Entry(ticket).State == EntityState.Detached)
        {
            _context.ProviderSupportTickets.Update(ticket);
        }

        await _context.SaveChangesAsync();
    }

    public Task<ProviderSupportTicket?> GetByIdAsync(Guid id) =>
        _context.ProviderSupportTickets
            .Include(t => t.Comments)
            .FirstOrDefaultAsync(t => t.Id == id);

    public async Task<IReadOnlyList<ProviderSupportTicket>> ListByProviderAsync(Guid providerId) =>
        await _context.ProviderSupportTickets
            .Where(t => t.ProviderId == providerId)
            .OrderByDescending(t => t.CreatedAtUtc)
            .ToListAsync();

    public async Task<AdminProviderSupportTicketRow?> GetAdminRowByIdAsync(Guid id)
    {
        var ticket = await _context.ProviderSupportTickets
            .Include(t => t.Comments)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket is null)
        {
            return null;
        }

        string providerName = await _context.Providers
            .Where(p => p.Id == ticket.ProviderId)
            .Select(p => p.DisplayName)
            .FirstOrDefaultAsync() ?? "(unknown provider)";

        return new AdminProviderSupportTicketRow(ticket, providerName);
    }

    public async Task<AdminProviderSupportTicketSearchResult> SearchAsync(AdminProviderSupportTicketCriteria criteria, int page, int pageSize)
    {
        var filtered = ApplyFilters(_context.ProviderSupportTickets, criteria);

        // Same reasoning as SupportTicketRepository.SearchAsync: count/page the
        // plain ticket query first, join display names only over the
        // already-paged rows.
        int totalCount = await filtered.CountAsync();

        var pagedTickets = filtered
            .OrderByDescending(t => t.CreatedAtUtc)
            .ApplyPaging(page, pageSize);

        var rows = await JoinNames(pagedTickets).ToListAsync();

        var ordered = rows.OrderByDescending(r => r.Ticket.CreatedAtUtc).ToList();

        return new AdminProviderSupportTicketSearchResult(ordered, totalCount);
    }

    private static IQueryable<ProviderSupportTicket> ApplyFilters(IQueryable<ProviderSupportTicket> query, AdminProviderSupportTicketCriteria criteria)
    {
        if (criteria.ProviderId.HasValue)
        {
            query = query.Where(t => t.ProviderId == criteria.ProviderId.Value);
        }

        if (criteria.Category.HasValue)
        {
            query = query.Where(t => t.Category == criteria.Category.Value);
        }

        if (criteria.Status.HasValue)
        {
            query = query.Where(t => t.Status == criteria.Status.Value);
        }

        if (criteria.FromUtc.HasValue)
        {
            query = query.Where(t => t.CreatedAtUtc >= criteria.FromUtc.Value);
        }

        if (criteria.ToUtc.HasValue)
        {
            query = query.Where(t => t.CreatedAtUtc <= criteria.ToUtc.Value);
        }

        return query;
    }

    private IQueryable<AdminProviderSupportTicketRow> JoinNames(IQueryable<ProviderSupportTicket> tickets)
    {
        var query =
            from ticket in tickets
            join provider in _context.Providers on ticket.ProviderId equals provider.Id
            select new AdminProviderSupportTicketRow(ticket, provider.DisplayName);

        return query;
    }
}
