using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Customers;

/// <summary>
/// Admin customer management (SRS 12.4, tasks 101a-101d): search/filter, the
/// 360 detail view, block/unblock, and internal notes. One service rather
/// than four, since every operation shares the same "load the customer,
/// fail with NotFound if missing" shape and the same set of collaborating
/// repositories (bookings, wallet, coupons, support tickets, notes).
/// </summary>
public interface ICustomerManagementService
{
    Task<Result<CustomerSearchResponse>> SearchAsync(CustomerSearchRequest request);

    Task<Result<CustomerDetailResponse>> GetDetailAsync(Guid customerId);

    /// <summary>Blocks a customer's account (SRS 12.4.3). <paramref name="adminUserId"/> is the acting admin, for audit.</summary>
    Task<Result<CustomerDetailResponse>> BlockAsync(Guid customerId, Guid adminUserId, string reason);

    /// <summary>Restores a blocked customer's account to Active (SRS 12.4.3).</summary>
    Task<Result<CustomerDetailResponse>> UnblockAsync(Guid customerId, Guid adminUserId);

    Task<Result<CustomerNoteResponse>> AddNoteAsync(Guid customerId, Guid adminUserId, string note);

    /// <summary>
    /// Support-initiated account deletion (right-to-erasure request handled
    /// on the customer's behalf). Terminal and irreversible - unlike
    /// Block/Unblock there is no "undelete". <paramref name="adminUserId"/>
    /// is the acting admin, for audit.
    /// </summary>
    Task<Result<CustomerDetailResponse>> DeleteAsync(Guid customerId, Guid adminUserId, string reason);

    /// <summary>
    /// Manual wallet credit/debit (SRS 12.4.3 gap: the wallet tab was
    /// read-only with no way to issue a goodwill credit or a correction).
    /// Delegates to <see cref="Nestly.Application.Wallet.IWalletService"/> for
    /// the actual ledger write, so the same balance/FIFO-consumption
    /// guarantees apply as every system-driven wallet credit/debit.
    /// <paramref name="adminUserId"/> is the acting admin, for audit.
    /// </summary>
    Task<Result<CustomerDetailResponse>> AdjustWalletAsync(Guid customerId, Guid adminUserId, AdjustCustomerWalletRequest request);

    /// <summary>
    /// The Customer Analytics dashboard's KPI counts and registration-trend
    /// series (Admin Web new page, customer counterpart to the Provider
    /// Onboarding Overview/Performance dashboards) - see
    /// <see cref="CustomerAnalyticsResponse"/>'s doc comment for exactly
    /// what each field means and what was deliberately left out.
    /// </summary>
    Task<Result<CustomerAnalyticsResponse>> GetAnalyticsAsync(CustomerAnalyticsRequest request);
}
