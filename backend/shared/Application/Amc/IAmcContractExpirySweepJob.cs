namespace Nestly.Application.Amc;

/// <summary>
/// Scheduled sweep for <see cref="Nestly.Domain.CustomerAmcContract"/> (docs/AMC.md):
/// advances a still-<c>Active</c> contract whose term has passed to
/// <c>Expired</c>, and raises the "expiring soon" reminder ahead of a term's
/// end - the same two responsibilities <see cref="Nestly.Application.Subscriptions.ISubscriptionBillingJob"/>'s
/// <c>NotifyExpiringSoonAsync</c> half covers for Subscription, minus the
/// billing half (AMC has no recurring charge to attempt - docs/AMC.md OPEN
/// DECISIONS #1, auto-renewal is out of MVP scope).
///
/// Both repository queries this job drives (<c>ListPastTermStillActiveAsync</c>,
/// <c>ListNeedingExpiringSoonNotificationAsync</c>) and both domain methods it
/// calls (<see cref="Nestly.Domain.CustomerAmcContract.Expire"/>,
/// <see cref="Nestly.Domain.CustomerAmcContract.MarkExpiringSoonNotified"/>)
/// already existed - this interface is what was missing to actually invoke
/// them on a schedule, registered as a Hangfire recurring job the same way
/// as <c>ISubscriptionBillingJob</c> (see admin-api <c>Program.cs</c>).
/// </summary>
public interface IAmcContractExpirySweepJob
{
    /// <summary>Idempotent and safe to re-run (Hangfire's retry convention requires this): an already-Expired contract is no longer Active so <c>ListPastTermStillActiveAsync</c> excludes it, and an already-notified contract's <c>ExpiringSoonNotifiedForEndDateUtc</c> already equals its current <c>EndDateUtc</c> so <c>ListNeedingExpiringSoonNotificationAsync</c> excludes it too.</summary>
    Task SweepAsync(CancellationToken cancellationToken = default);
}
