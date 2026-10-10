namespace Nestly.Application.Wallet;

/// <summary>
/// Hangfire job: the safety net under wallet top-ups. A customer can pay at the gateway and have the webhook
/// fail to reach us, or never come back to the return page; without this their money would sit taken but
/// uncredited. Every pending top-up old enough to have been abandoned or lost is checked directly with the
/// gateway and given its real outcome. Idempotent - resolving a top-up is guarded, so re-running or racing
/// the webhook is harmless.
/// </summary>
public interface IWalletTopUpSweepJob
{
    Task SweepAsync(CancellationToken cancellationToken = default);
}
