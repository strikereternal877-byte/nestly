using Nestly.Application;
using Nestly.Application.ProviderManagement;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderPayoutService"/>
public class ProviderPayoutService : IProviderPayoutService
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderPayoutRepository _payoutRepository;
    private readonly IProviderEarningLedgerRepository _ledgerRepository;

    public ProviderPayoutService(
        IProviderRepository providerRepository,
        IProviderPayoutRepository payoutRepository,
        IProviderEarningLedgerRepository ledgerRepository)
    {
        _providerRepository = providerRepository;
        _payoutRepository = payoutRepository;
        _ledgerRepository = ledgerRepository;
    }

    public async Task<Result<ProviderPayoutResponse>> CreateBatchAsync(Guid providerId, CreateProviderPayoutRequest request)
    {
        var provider = await _providerRepository.GetByIdAsync(providerId);
        if (provider is null)
        {
            return Error.NotFound("ProviderPayout.ProviderNotFound", "Provider was not found.");
        }

        var entries = await _ledgerRepository.ListByProviderAndPeriodAsync(providerId, request.PeriodStart, request.PeriodEnd);
        decimal total = entries.Sum(e => e.EntryType == ProviderEarningEntryType.Credit ? e.Amount : -e.Amount);

        if (total <= 0)
        {
            return Error.Business("ProviderPayout.NothingToPay", "There is no positive earning balance for this provider in the given period.");
        }

        var payout = new ProviderPayout(Guid.NewGuid(), providerId, request.PeriodStart, request.PeriodEnd, total);
        await _payoutRepository.AddAsync(payout);

        return ToResponse(payout, provider.DisplayName);
    }

    public async Task<Result<ProviderPayoutResponse>> GetByIdAsync(Guid payoutId)
    {
        var payout = await _payoutRepository.GetByIdAsync(payoutId);
        if (payout is null)
        {
            return Error.NotFound("ProviderPayout.NotFound", "Payout was not found.");
        }

        var provider = await _providerRepository.GetByIdAsync(payout.ProviderId);
        return ToResponse(payout, provider?.DisplayName ?? "(unknown provider)");
    }

    public async Task<Result<ProviderPayoutSearchResponse>> SearchAsync(Guid? providerId, ProviderPayoutStatus? status, int page, int pageSize)
    {
        var (rows, totalCount) = await _payoutRepository.SearchAsync(providerId, status, page, pageSize);

        var providerCache = new Dictionary<Guid, string>();
        var items = new List<ProviderPayoutResponse>();
        foreach (var payout in rows)
        {
            if (!providerCache.TryGetValue(payout.ProviderId, out var displayName))
            {
                var provider = await _providerRepository.GetByIdAsync(payout.ProviderId);
                displayName = provider?.DisplayName ?? "(unknown provider)";
                providerCache[payout.ProviderId] = displayName;
            }

            items.Add(ToResponse(payout, displayName));
        }

        return new ProviderPayoutSearchResponse(items, totalCount, page, pageSize);
    }

    public async Task<Result<ProviderPayoutResponse>> UpdateStatusAsync(Guid payoutId, UpdateProviderPayoutStatusRequest request)
    {
        var payout = await _payoutRepository.GetByIdAsync(payoutId);
        if (payout is null)
        {
            return Error.NotFound("ProviderPayout.NotFound", "Payout was not found.");
        }

        try
        {
            switch (request.Status)
            {
                case ProviderPayoutStatus.Processing:
                    payout.MarkProcessing();
                    break;
                case ProviderPayoutStatus.Paid:
                    if (string.IsNullOrWhiteSpace(request.PayoutReference))
                    {
                        return Error.Validation("ProviderPayout.ReferenceRequired", "A payout reference is required to mark a payout paid.");
                    }

                    payout.MarkPaid(request.PayoutReference);
                    break;
                case ProviderPayoutStatus.Failed:
                    payout.MarkFailed(request.Notes);
                    break;
                default:
                    return Error.Validation("ProviderPayout.InvalidTargetStatus", "A payout can only be moved to Processing, Paid or Failed.");
            }
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("ProviderPayout.InvalidTransition", ex.Message);
        }

        await _payoutRepository.UpdateAsync(payout);

        var provider = await _providerRepository.GetByIdAsync(payout.ProviderId);
        return ToResponse(payout, provider?.DisplayName ?? "(unknown provider)");
    }

    private static ProviderPayoutResponse ToResponse(ProviderPayout payout, string providerDisplayName) => new(
        payout.Id, payout.ProviderId, providerDisplayName, payout.PeriodStart, payout.PeriodEnd,
        payout.TotalAmount, payout.Status, payout.PayoutReference, payout.Notes, payout.CreatedAt, payout.UpdatedAt);
}
