using System.ComponentModel.DataAnnotations;

namespace Nestly.Infrastructure.Options;

/// <summary>
/// Strongly typed binding of the "AmcExpiry" configuration section
/// (docs/AMC.md's scheduled expiry sweep). Not a secret, has a safe
/// production-sensible default - same reasoning as
/// <see cref="SubscriptionBillingOptions"/>.
/// </summary>
public class AmcExpiryOptions
{
    public const string SectionName = "AmcExpiry";

    /// <summary>
    /// How many days ahead of an AMC contract's <see cref="Domain.CustomerAmcContract.EndDateUtc"/>
    /// the "expiring soon" reminder fires. 30 rather than
    /// <see cref="SubscriptionBillingOptions.ExpiringSoonLeadTimeDays"/>'s 3:
    /// an AMC term runs "typically 12 months" (docs/AMC.md) versus a
    /// subscription's monthly/quarterly cycle, so a lead time proportioned
    /// the same way as a fraction of the term is meaningfully longer here -
    /// a customer deciding whether to renew a year-long contract needs more
    /// notice than one whose next charge is a few days out.
    /// </summary>
    [Range(1, 90)]
    public int ExpiringSoonLeadTimeDays { get; set; } = 30;
}
