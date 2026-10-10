namespace Nestly.Domain;

/// <summary>
/// A provider support ticket's category (Provider Management UX pass) -
/// deliberately a smaller, provider-relevant set than
/// <see cref="SupportTicketCategory"/> (the customer ticket system's own
/// category list, all about bookings/payments/service quality - none of
/// which describe what a provider needs help with).
/// </summary>
public enum ProviderSupportTicketCategory
{
    Payout,
    Kyc,
    JobIssue,
    Account,
    Technical,
    Other
}
