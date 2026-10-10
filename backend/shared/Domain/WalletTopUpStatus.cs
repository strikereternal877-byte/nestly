namespace Nestly.Domain;

/// <summary>Lifecycle of a <see cref="WalletTopUp"/>. Stored as a string (max length 20).</summary>
public enum WalletTopUpStatus
{
    /// <summary>The gateway order exists and no outcome has been applied yet.</summary>
    Pending,

    /// <summary>The gateway reported success and the amount was credited to the wallet in the same database transaction.</summary>
    Success,

    /// <summary>The gateway reported failure, or the checkout was abandoned; nothing was credited.</summary>
    Failed
}
