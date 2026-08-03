using Nestly.Application;
using Nestly.Application.ProviderManagement;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="IProviderEarningLedgerService"/>
public class ProviderEarningLedgerService : IProviderEarningLedgerService
{
    private readonly IProviderRepository _providerRepository;
    private readonly IProviderEarningLedgerRepository _ledgerRepository;

    public ProviderEarningLedgerService(IProviderRepository providerRepository, IProviderEarningLedgerRepository ledgerRepository)
    {
        _providerRepository = providerRepository;
        _ledgerRepository = ledgerRepository;
    }

    public async Task<Result<ProviderEarningLedgerEntryResponse>> RecordAdjustmentAsync(Guid providerId, RecordProviderEarningAdjustmentRequest request)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Error.NotFound("ProviderEarningLedger.ProviderNotFound", "Provider was not found.");
        }

        var latest = await _ledgerRepository.GetLatestAsync(providerId);
        decimal currentBalance = latest?.BalanceAfter ?? 0m;

        decimal newBalance = request.EntryType == ProviderEarningEntryType.Credit
            ? currentBalance + request.Amount
            : currentBalance - request.Amount;

        if (newBalance < 0)
        {
            return Error.Business("ProviderEarningLedger.InsufficientBalance", "This debit would take the provider's earnings balance negative.");
        }

        var entry = new ProviderEarningLedgerEntry(
            Guid.NewGuid(), providerId, request.EntryType, request.Amount, newBalance,
            request.SourceType, request.SourceReferenceId, request.Description);
        await _ledgerRepository.AddAsync(entry);

        return ToResponse(entry);
    }

    public async Task<Result<ProviderEarningsSummaryResponse>> GetSummaryAsync(Guid providerId)
    {
        if (!await _providerRepository.ExistsAsync(providerId))
        {
            return Error.NotFound("ProviderEarningLedger.ProviderNotFound", "Provider was not found.");
        }

        var entries = await _ledgerRepository.ListByProviderAsync(providerId);
        decimal balance = entries.Count > 0 ? entries[0].BalanceAfter : 0m;

        return new ProviderEarningsSummaryResponse(providerId, balance, entries.Select(ToResponse).ToList());
    }

    private static ProviderEarningLedgerEntryResponse ToResponse(ProviderEarningLedgerEntry entry) => new(
        entry.Id, entry.ProviderId, entry.EntryType, entry.Amount, entry.BalanceAfter,
        entry.SourceType, entry.SourceReferenceId, entry.Description, entry.CreatedAtUtc);
}
