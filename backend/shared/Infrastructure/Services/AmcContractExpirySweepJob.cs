using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application.Amc;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>See <see cref="IAmcContractExpirySweepJob"/>.</summary>
public class AmcContractExpirySweepJob : IAmcContractExpirySweepJob
{
    private readonly ICustomerAmcContractRepository _contractRepository;
    private readonly AmcExpiryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AmcContractExpirySweepJob> _logger;

    public AmcContractExpirySweepJob(
        ICustomerAmcContractRepository contractRepository,
        IOptions<AmcExpiryOptions> options,
        TimeProvider timeProvider,
        ILogger<AmcContractExpirySweepJob> logger)
    {
        _contractRepository = contractRepository;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        await ExpireOverdueAsync(nowUtc, cancellationToken);
        await NotifyExpiringSoonAsync(nowUtc, cancellationToken);
    }

    /// <summary>
    /// Moves every still-Active contract whose term has already ended to
    /// Expired. Without this, <see cref="Nestly.Domain.CustomerAmcContract.Status"/>
    /// stays Active forever past term end - the domain model's own
    /// <see cref="Nestly.Domain.CustomerAmcContract.CanRedeem"/> check still
    /// correctly blocks a redemption against it (it re-checks
    /// <c>EndDateUtc</c> directly), so this is a reporting/visibility gap,
    /// not a security one: without it, the admin renewal report's ByStatus
    /// tile undercounts Expired and overcounts Active, and a contract whose
    /// term lapsed outside the report's horizon window becomes permanently
    /// invisible to the renewal pipeline the report exists to drive
    /// (docs/AMC.md "best cash-flow profile... only realizes if
    /// expiring/exhausted contracts actually get renewed").
    /// </summary>
    private async Task ExpireOverdueAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var overdue = await _contractRepository.ListPastTermStillActiveAsync(nowUtc);

        int expired = 0;
        foreach (var contract in overdue)
        {
            cancellationToken.ThrowIfCancellationRequested();

            contract.Expire(nowUtc);
            await _contractRepository.UpdateAsync(contract);
            expired++;
        }

        if (expired > 0)
        {
            _logger.LogInformation("AMC contract expiry sweep: {ExpiredCount} contract(s) moved Active -> Expired.", expired);
        }
    }

    /// <summary>
    /// Raises the "expiring soon" reminder for every Active contract whose
    /// term ends within the configured lead time and hasn't already been
    /// notified for this term end - mirrors <see cref="SubscriptionBillingJob.NotifyExpiringSoonAsync"/>.
    /// </summary>
    private async Task NotifyExpiringSoonAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var windowEndUtc = nowUtc.AddDays(_options.ExpiringSoonLeadTimeDays);
        var expiringSoon = await _contractRepository.ListNeedingExpiringSoonNotificationAsync(nowUtc, windowEndUtc);

        foreach (var contract in expiringSoon)
        {
            cancellationToken.ThrowIfCancellationRequested();

            contract.MarkExpiringSoonNotified(nowUtc);
            await _contractRepository.UpdateAsync(contract);
        }

        if (expiringSoon.Count > 0)
        {
            _logger.LogInformation("AMC contract expiry sweep: {Count} contract(s) notified as expiring soon.", expiringSoon.Count);
        }
    }
}
