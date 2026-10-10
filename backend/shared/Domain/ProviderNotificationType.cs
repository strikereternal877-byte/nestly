namespace Nestly.Domain;

/// <summary>
/// What triggered a <see cref="ProviderNotification"/> (Provider Management UX
/// pass: providers had no in-app notification feed at all - see
/// <c>ProviderNotificationPublisher</c>'s doc comment for the full gap this
/// closes). Deliberately a much smaller set than <see cref="NotificationEventType"/>:
/// that enum covers every customer-facing SMS/email/push trigger built up
/// over many tasks, where this one only covers what a provider needs to know
/// to run their day - append-only for the same wire-format reason
/// <see cref="NotificationEventType"/> documents (provider-api registers no
/// JsonStringEnumConverter, and the column is stored as this name via
/// HasConversion&lt;string&gt;() rather than the ordinal, but callers must
/// still never renumber).
/// </summary>
public enum ProviderNotificationType
{
    /// <summary>A booking was assigned/offered to this provider (admin or auto-assignment) - the single most time-sensitive event a provider can miss.</summary>
    JobOffered,

    /// <summary>An admin rejected a submitted KYC document.</summary>
    KycRejected,

    /// <summary>An admin suspended this provider's account.</summary>
    Suspended,

    /// <summary>A payout batch was marked Paid.</summary>
    PayoutProcessed,

    /// <summary>An admin replied to or resolved the provider's support ticket.</summary>
    SupportTicketReply,

    /// <summary>An admin approved the provider's submitted bank account details.</summary>
    BankAccountApproved,

    /// <summary>An admin rejected the provider's submitted bank account details.</summary>
    BankAccountRejected,

    /// <summary>A job assigned to this provider was moved to a new time (by the customer or an admin) and it is still theirs.</summary>
    JobRescheduled,

    /// <summary>A job was taken off this provider - the customer moved it to a time that does not work with their schedule.</summary>
    JobUnassigned,

    /// <summary>The booking a professional was assigned to was cancelled (by the customer or an admin).</summary>
    JobCancelled,
}
