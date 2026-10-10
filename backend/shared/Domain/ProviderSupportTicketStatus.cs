namespace Nestly.Domain;

/// <summary>
/// A provider support ticket's lifecycle status - deliberately a smaller set
/// than <see cref="SupportTicketStatus"/>: no Escalated/WaitingForCustomer/
/// Closed, since the provider ticket workflow (Provider Management UX pass)
/// has no assignment, dispute or escalation machinery. See
/// <see cref="ProviderSupportTicketLifecycle"/> for the transition matrix.
/// </summary>
public enum ProviderSupportTicketStatus
{
    Open,
    InProgress,
    Resolved
}
