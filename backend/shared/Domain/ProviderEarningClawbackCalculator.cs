namespace Nestly.Domain;

/// <summary>
/// Pure "how much of a provider's already-credited job-completion earning
/// must be clawed back for a later refund" math. Mirrors
/// <see cref="CommissionCalculator"/>'s split: this only turns an
/// already-known original gross/net credit plus a refund amount into a
/// rupee-and-paise clawback figure, so the arithmetic can be unit tested
/// with no DI/repository plumbing at all.
///
/// A provider is only ever credited a share of the booking's GATEWAY
/// payment - escrow never holds the wallet-funded portion (see
/// <c>EscrowService.HoldAsync</c>) - so a refund that itself draws from
/// both sources must pass only its payment-funded share as
/// <paramref name="paymentFundedRefundAmount"/>; <c>RefundService</c>
/// already isolates that as its payment settlement's own amount.
/// </summary>
public static class ProviderEarningClawbackCalculator
{
    /// <param name="originalGrossAmount">The gateway payment the provider's net credit was originally a share of (0 if nothing was ever credited).</param>
    /// <param name="originalNetAmountToProvider">What the provider was actually credited when the booking completed - the ceiling this can never exceed.</param>
    /// <param name="paymentFundedRefundAmount">The gateway-funded portion of THIS refund - never the wallet-funded portion, which the provider was never paid in the first place.</param>
    public static decimal Compute(decimal originalGrossAmount, decimal originalNetAmountToProvider, decimal paymentFundedRefundAmount)
    {
        if (originalGrossAmount < 0 || originalNetAmountToProvider < 0 || paymentFundedRefundAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(originalGrossAmount), "Amounts cannot be negative.");
        }

        if (originalNetAmountToProvider > originalGrossAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(originalNetAmountToProvider), "A provider's net credit cannot exceed the gross payment it was a share of.");
        }

        if (originalGrossAmount == 0 || originalNetAmountToProvider == 0 || paymentFundedRefundAmount == 0)
        {
            return 0m;
        }

        // Same share of the refund as the provider was originally credited
        // out of the gross payment - a full refund claws back the full net
        // credit, a partial refund claws back the matching slice of it.
        decimal proportional = paymentFundedRefundAmount * originalNetAmountToProvider / originalGrossAmount;
        return Math.Min(Math.Round(proportional, 2, MidpointRounding.ToEven), originalNetAmountToProvider);
    }
}
