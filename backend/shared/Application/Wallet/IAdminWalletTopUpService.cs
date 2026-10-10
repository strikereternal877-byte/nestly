using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Wallet;

/// <summary>
/// The admin's view of customers' wallet top-ups: find them, see which need attention, and ask the gateway about a
/// stuck one. Read-mostly - the only write is the gateway check, which uses the same code path as the background
/// sweep, so an admin can never credit a wallet by hand through here (a manual credit is the customer page's
/// wallet adjustment, which is audited separately).
/// </summary>
public interface IAdminWalletTopUpService
{
    /// <summary>A Pending top-up older than this is "stuck": the sweep checks the gateway every few minutes, so it should have resolved long before.</summary>
    const int StuckAfterMinutes = 30;

    Task<Result<PagedAdminWalletTopUpResponse>> SearchAsync(AdminWalletTopUpFilterRequest filter);

    /// <summary>NotFound ("WalletTopUp.NotFound") when the id is not a top-up.</summary>
    Task<Result<AdminWalletTopUpResponse>> GetAsync(Guid topUpId);

    /// <summary>
    /// Asks the gateway how the top-up ended and applies a definite answer, then records who did it. Safe to repeat:
    /// the credit is guarded by the same conditional update as a webhook, so it can only ever land once.
    /// </summary>
    Task<Result<AdminWalletTopUpReconcileResponse>> ReconcileAsync(Guid topUpId, Guid adminUserId);
}
