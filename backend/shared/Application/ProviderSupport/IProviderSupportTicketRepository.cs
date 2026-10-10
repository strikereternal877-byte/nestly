using Nestly.Domain;

namespace Nestly.Application.ProviderSupport;

public interface IProviderSupportTicketRepository
{
    Task AddAsync(ProviderSupportTicket ticket);

    Task UpdateAsync(ProviderSupportTicket ticket);

    /// <summary>Loaded with its comment thread - a ticket is never useful partially loaded.</summary>
    Task<ProviderSupportTicket?> GetByIdAsync(Guid id);

    Task<IReadOnlyList<ProviderSupportTicket>> ListByProviderAsync(Guid providerId);

    /// <summary>One ticket joined with its provider's display name, for the admin detail screen. Null if no ticket with that id exists.</summary>
    Task<AdminProviderSupportTicketRow?> GetAdminRowByIdAsync(Guid id);

    /// <summary>Filtered/paginated admin ticket search across every provider - unlike <see cref="ListByProviderAsync"/>, not scoped to a single provider.</summary>
    Task<AdminProviderSupportTicketSearchResult> SearchAsync(AdminProviderSupportTicketCriteria criteria, int page, int pageSize);
}
