using System.ComponentModel.DataAnnotations;

namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "WalletTopUp" configuration section: letting a customer add their own
/// money to their wallet through the payment gateway.
///
/// <para>
/// <see cref="Enabled"/> is <b>false by default and must stay false in production until the business and
/// legal groundwork for holding customers' money is done</b> (whether a closed wallet of this kind needs
/// regulatory approval, GST and accounting treatment, the terms shown to customers, and the payment
/// provider's own agreement). The amounts below are conservative starting points, not decisions - they
/// are configuration precisely so the business can set them without a deployment.
/// </para>
/// </summary>
public class WalletTopUpOptions
{
    public const string SectionName = "WalletTopUp";

    /// <summary>Master switch. Off: the API refuses to start a top-up and the screens do not offer one.</summary>
    public bool Enabled { get; set; }

    /// <summary>Smallest single top-up, in rupees.</summary>
    [Range(1, 100000)]
    public decimal MinAmount { get; set; } = 100m;

    /// <summary>Largest single top-up, in rupees.</summary>
    [Range(1, 1000000)]
    public decimal MaxAmount { get; set; } = 10000m;

    /// <summary>
    /// The most a wallet may hold after a top-up lands (counting top-ups still awaiting the gateway). Keeps the
    /// amount of customer money the business is liable for - and exposed to a fraud or chargeback - bounded.
    /// </summary>
    [Range(1, 10000000)]
    public decimal MaxWalletBalance { get; set; } = 20000m;

    /// <summary>How many top-ups one customer may start in 24 hours, whatever their outcome - blunts card-testing.</summary>
    [Range(1, 200)]
    public int MaxTopUpsPerDay { get; set; } = 10;

    /// <summary>A still-pending top-up of the same amount started within this many minutes is reused rather than duplicated (double-click, retry, reload).</summary>
    [Range(1, 120)]
    public int PendingReuseMinutes { get; set; } = 15;

    /// <summary>The reconciliation sweep checks a pending top-up with the gateway once it is at least this many minutes old.</summary>
    [Range(1, 1440)]
    public int ReconcileAfterMinutes { get; set; } = 15;

    /// <summary>...and stops looking at it after this many days.</summary>
    [Range(1, 30)]
    public int ReconcileUpToDays { get; set; } = 7;

    /// <summary>Quick-pick amounts offered on the screen, comma separated. Only those inside [MinAmount, MaxAmount] are shown.</summary>
    public string SuggestedAmounts { get; set; } = "500,1000,2000,5000";
}
