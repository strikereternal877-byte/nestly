using Nestly.Application.ProviderEarnings;
using Nestly.Application.ProviderManagement;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderEarningsService"/>
public class ProviderEarningsService : IProviderEarningsService
{
    private readonly IProviderEarningLedgerService _ledgerService;
    private readonly IProviderPayoutService _payoutService;

    public ProviderEarningsService(IProviderEarningLedgerService ledgerService, IProviderPayoutService payoutService)
    {
        _ledgerService = ledgerService;
        _payoutService = payoutService;
    }

    public Task<Result<ProviderEarningsSummaryResponse>> GetSummaryAsync(Guid providerId) =>
        _ledgerService.GetSummaryAsync(providerId);

    public async Task<Result<IReadOnlyList<ProviderEarningLedgerEntryResponse>>> GetLedgerAsync(Guid providerId)
    {
        var summary = await _ledgerService.GetSummaryAsync(providerId);
        return summary.IsSuccess
            ? Result.Success<IReadOnlyList<ProviderEarningLedgerEntryResponse>>(summary.Value.Entries)
            : summary.Error;
    }

    public Task<Result<ProviderPayoutSearchResponse>> ListPayoutsAsync(Guid providerId, ProviderPayoutStatus? status, int page, int pageSize) =>
        _payoutService.SearchAsync(providerId, status, page, pageSize);

    public async Task<Result<ProviderPayoutResponse>> GetPayoutDetailAsync(Guid providerId, Guid payoutId)
    {
        var result = await _payoutService.GetByIdAsync(payoutId);
        if (result.IsFailure)
        {
            return result;
        }

        if (result.Value.ProviderId != providerId)
        {
            // Same code/message as a genuine not-found - never confirms
            // another provider's payout id exists (SRS 28.3 IDOR).
            return Error.NotFound("ProviderPayout.NotFound", "Payout was not found.");
        }

        return result;
    }
}
