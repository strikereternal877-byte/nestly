using Microsoft.Extensions.Logging;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.NestlyCoins;
using Nestly.Application.ProviderManagement;
using Nestly.Application.Wallet;
using Nestly.Domain;
using Nestly.Domain.NestlyCoins;

namespace Nestly.Infrastructure.Services;

/// <inheritdoc cref="INestlyCoinsService"/>
public class NestlyCoinsService : INestlyCoinsService
{
    private readonly INestlyCoinsProgramConfigRepository _configRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IWalletService _walletService;
    private readonly IWalletLedgerRepository _walletLedgerRepository;
    private readonly IProviderEarningLedgerService _providerEarningLedgerService;
    private readonly IProviderEarningLedgerRepository _providerEarningLedgerRepository;
    private readonly ILogger<NestlyCoinsService> _logger;

    public NestlyCoinsService(
        INestlyCoinsProgramConfigRepository configRepository,
        IBookingRepository bookingRepository,
        IWalletService walletService,
        IWalletLedgerRepository walletLedgerRepository,
        IProviderEarningLedgerService providerEarningLedgerService,
        IProviderEarningLedgerRepository providerEarningLedgerRepository,
        ILogger<NestlyCoinsService> logger)
    {
        _configRepository = configRepository;
        _bookingRepository = bookingRepository;
        _walletService = walletService;
        _walletLedgerRepository = walletLedgerRepository;
        _providerEarningLedgerService = providerEarningLedgerService;
        _providerEarningLedgerRepository = providerEarningLedgerRepository;
        _logger = logger;
    }

    public bool EvaluateQualifyingOrder(NestlyCoinsProgramConfig config, decimal orderAmount, int priorCompletedCount, decimal creditedThisMonth)
    {
        if (!config.IsActive)
        {
            return false;
        }

        if (orderAmount < config.MinimumOrderAmount)
        {
            return false;
        }

        if (config.RequireReorder && priorCompletedCount == 0)
        {
            return false;
        }

        decimal earned = CalculateEarnAmount(config, orderAmount);
        if (config.MaxCoinsPerMonth is decimal cap && creditedThisMonth + earned > cap)
        {
            return false;
        }

        return earned > 0;
    }

    public async Task CreditCustomerCoinsAsync(Booking booking)
    {
        var config = await _configRepository.GetByAudienceAsync(NestlyCoinsAudience.Customer);
        if (config is null)
        {
            return;
        }

        int priorCompleted = await _bookingRepository.CountCompletedByCustomerAsync(booking.CustomerId, booking.Id);
        decimal creditedThisMonth = await _walletLedgerRepository.SumCreditsBySourceTypeInRangeAsync(
            booking.CustomerId, WalletSourceType.NestlyCoinsReward, CurrentMonthStartUtc(), NextMonthStartUtc());

        if (!EvaluateQualifyingOrder(config, booking.TotalPayableSnapshot, priorCompleted, creditedThisMonth))
        {
            return;
        }

        decimal amount = CalculateEarnAmount(config, booking.TotalPayableSnapshot);

        // Coins always carry an expiry (GUIDELINES #3) - this is what makes
        // WalletService.CreditAsync's FIFO consumption tracking (task 175,
        // confirmed working against main as part of task 199's resolution)
        // apply to these credits.
        await _walletService.CreditAsync(
            booking.CustomerId, amount, WalletSourceType.NestlyCoinsReward, booking.Id,
            $"Nestly Coins earned - booking {booking.Id}.",
            expiresAtUtc: DateTime.UtcNow.AddDays(config.ExpiryDays));
    }

    public async Task CreditProviderCoinsAsync(Booking booking)
    {
        if (booking.AssignedProviderId is not Guid providerId)
        {
            return;
        }

        var config = await _configRepository.GetByAudienceAsync(NestlyCoinsAudience.Provider);
        if (config is null)
        {
            return;
        }

        int priorCompleted = await _bookingRepository.CountCompletedByAssignedProviderAsync(providerId, booking.Id);
        decimal creditedThisMonth = await _providerEarningLedgerRepository.SumCreditsBySourceTypeInRangeAsync(
            providerId, ProviderEarningSourceType.NestlyCoinsReward, CurrentMonthStartUtc(), NextMonthStartUtc());

        if (!EvaluateQualifyingOrder(config, booking.TotalPayableSnapshot, priorCompleted, creditedThisMonth))
        {
            return;
        }

        decimal amount = CalculateEarnAmount(config, booking.TotalPayableSnapshot);

        // Unlike WalletLedgerEntry, ProviderEarningLedgerEntry has no
        // ExpiresAtUtc/RemainingAmount - the provider earning ledger settles
        // via periodic ProviderPayout batches rather than per-item spend-down,
        // so there is no equivalent per-entry expiry to set here. This is a
        // real architectural asymmetry, not an oversight: GUIDELINES #3's
        // FIFO-expiry prerequisite is specifically about WalletLedgerEntry.
        var result = await _providerEarningLedgerService.RecordAdjustmentAsync(
            providerId,
            new RecordProviderEarningAdjustmentRequest(
                ProviderEarningEntryType.Credit,
                amount,
                ProviderEarningSourceType.NestlyCoinsReward,
                booking.Id,
                $"Nestly Coins earned - booking {booking.Id}."));

        if (result.IsFailure)
        {
            // Fire-and-forget domain event handler, not inside the caller's
            // own unit of work (same reasoning EscrowReleaseOnCompletionHandler
            // already established for this exact call) - logged for admin
            // reconciliation rather than thrown.
            _logger.LogWarning(
                "Failed to credit Nestly Coins to provider {ProviderId} for booking {BookingId}: {ErrorCode} {ErrorMessage}",
                providerId, booking.Id, result.Error.Code, result.Error.Message);
        }
    }

    public async Task ClawbackOnCancellationAsync(Guid bookingId)
    {
        await ClawbackCustomerCreditAsync(bookingId);
        await ClawbackProviderCreditAsync(bookingId);
    }

    private async Task ClawbackCustomerCreditAsync(Guid bookingId)
    {
        var credit = await _walletLedgerRepository.FindBySourceAsync(WalletSourceType.NestlyCoinsReward, bookingId);
        if (credit is null)
        {
            return;
        }

        var config = await _configRepository.GetByAudienceAsync(NestlyCoinsAudience.Customer);
        if (config is null || DateTime.UtcNow > credit.CreatedAtUtc.AddDays(config.ClawbackWindowDays))
        {
            return;
        }

        // Reverse only the still-unspent portion of THIS credit
        // (RemainingAmount, task 175's FIFO tracking) rather than the full
        // original Amount - a customer who already spent part of it
        // elsewhere must not have unrelated balance clawed back too.
        decimal amountToReverse = credit.RemainingAmount ?? credit.Amount;
        if (amountToReverse <= 0)
        {
            return;
        }

        var debitResult = await _walletService.DebitAsync(
            credit.CustomerId, amountToReverse, WalletSourceType.NestlyCoinsClawback, bookingId,
            $"Nestly Coins clawed back - booking {bookingId} cancelled within the clawback window.");

        if (debitResult.IsFailure)
        {
            _logger.LogWarning(
                "Failed to claw back Nestly Coins from customer {CustomerId} for cancelled booking {BookingId}: {ErrorCode} {ErrorMessage}",
                credit.CustomerId, bookingId, debitResult.Error.Code, debitResult.Error.Message);
        }
    }

    private async Task ClawbackProviderCreditAsync(Guid bookingId)
    {
        var credit = await _providerEarningLedgerRepository.FindBySourceAsync(ProviderEarningSourceType.NestlyCoinsReward, bookingId);
        if (credit is null)
        {
            return;
        }

        var config = await _configRepository.GetByAudienceAsync(NestlyCoinsAudience.Provider);
        if (config is null || DateTime.UtcNow > credit.CreatedAtUtc.AddDays(config.ClawbackWindowDays))
        {
            return;
        }

        // No RemainingAmount concept on ProviderEarningLedgerEntry (see
        // CreditProviderCoinsAsync's comment) - the full originally credited
        // amount is reversed, since there is no per-entry consumption
        // tracking to draw a partial figure from.
        var debitResult = await _providerEarningLedgerService.RecordAdjustmentAsync(
            credit.ProviderId,
            new RecordProviderEarningAdjustmentRequest(
                ProviderEarningEntryType.Debit,
                credit.Amount,
                ProviderEarningSourceType.NestlyCoinsClawback,
                bookingId,
                $"Nestly Coins clawed back - booking {bookingId} cancelled within the clawback window."));

        if (debitResult.IsFailure)
        {
            _logger.LogWarning(
                "Failed to claw back Nestly Coins from provider {ProviderId} for cancelled booking {BookingId}: {ErrorCode} {ErrorMessage}",
                credit.ProviderId, bookingId, debitResult.Error.Code, debitResult.Error.Message);
        }
    }

    private static decimal CalculateEarnAmount(NestlyCoinsProgramConfig config, decimal orderAmount) =>
        Math.Round(orderAmount / 100m * config.EarnRatePer100, 2, MidpointRounding.AwayFromZero);

    private static DateTime CurrentMonthStartUtc()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private static DateTime NextMonthStartUtc() => CurrentMonthStartUtc().AddMonths(1);
}
