namespace Nestly.Domain;

/// <summary>The provider support ticket state transition matrix - same separate-table-of-transitions approach as <see cref="SupportTicketLifecycle"/>, scaled down to this ticket's smaller status set.</summary>
public static class ProviderSupportTicketLifecycle
{
    private static readonly Dictionary<ProviderSupportTicketStatus, ProviderSupportTicketStatus[]> Transitions = new()
    {
        [ProviderSupportTicketStatus.Open] = [ProviderSupportTicketStatus.InProgress, ProviderSupportTicketStatus.Resolved],
        [ProviderSupportTicketStatus.InProgress] = [ProviderSupportTicketStatus.Resolved],
        // A provider (or admin) can reopen a resolution that did not actually fix things.
        [ProviderSupportTicketStatus.Resolved] = [ProviderSupportTicketStatus.InProgress],
    };

    public static bool IsValidTransition(ProviderSupportTicketStatus from, ProviderSupportTicketStatus to) =>
        Transitions[from].Contains(to);
}
