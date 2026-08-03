using Nestly.Domain;
using Nestly.Domain.NestlyCoins;

namespace Nestly.Application.NestlyCoins;

/// <summary>
/// Nestly Coins earn/credit/clawback (docs/NESTLY-COINS.md, task 201) - the
/// customer- and provider-side counterpart to Referral's
/// <c>ReferralQualifyingBookingHandler</c>/<c>IReferralRewardService</c>,
/// reusing the same wallet/provider-earning-ledger credit primitives rather
/// than inventing a parallel balance.
/// </summary>
public interface INestlyCoinsService
{
    /// <summary>
    /// Pure business-rule check (GUIDELINES #1-#2, FRAUD/ABUSE PREVENTION):
    /// does an order of <paramref name="orderAmount"/> qualify for a coins
    /// credit under <paramref name="config"/>, given how many OTHER
    /// Completed orders the subject (customer or provider) already has
    /// (<paramref name="priorCompletedCount"/>) and how much they've already
    /// been credited this calendar month (<paramref name="creditedThisMonth"/>)?
    /// No I/O - callers gather those inputs from the relevant repositories.
    /// </summary>
    bool EvaluateQualifyingOrder(NestlyCoinsProgramConfig config, decimal orderAmount, int priorCompletedCount, decimal creditedThisMonth);

    /// <summary>
    /// Credits the booking's customer's wallet if the Customer-audience
    /// program is active and <see cref="EvaluateQualifyingOrder"/> passes.
    /// No-op (never throws) when there is no Customer config, it is
    /// inactive, or the order doesn't qualify.
    /// </summary>
    Task CreditCustomerCoinsAsync(Booking booking);

    /// <summary>
    /// Credits the booking's assigned provider's earning ledger if the
    /// Provider-audience program is active and <see cref="EvaluateQualifyingOrder"/>
    /// passes. No-op when there is no assigned provider, no Provider config,
    /// it is inactive, or the order doesn't qualify.
    /// </summary>
    Task CreditProviderCoinsAsync(Booking booking);

    /// <summary>
    /// Reverses any Nestly Coins credit (customer and/or provider side)
    /// issued for this booking, if the cancellation falls within that
    /// side's ClawbackWindowDays of the original credit. No-op for a
    /// booking that was never credited, or one outside the window.
    /// </summary>
    Task ClawbackOnCancellationAsync(Guid bookingId);
}
