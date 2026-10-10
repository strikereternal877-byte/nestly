using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application.Wallet;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>See <see cref="IWalletTopUpSweepJob"/>.</summary>
public class WalletTopUpSweepJob : IWalletTopUpSweepJob
{
    /// <summary>One run's ceiling, so a backlog is worked off over several runs instead of one very long one.</summary>
    private const int BatchSize = 100;

    private readonly IWalletTopUpRepository _repository;
    private readonly IWalletTopUpService _service;
    private readonly WalletTopUpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WalletTopUpSweepJob> _logger;

    public WalletTopUpSweepJob(
        IWalletTopUpRepository repository,
        IWalletTopUpService service,
        IOptions<WalletTopUpOptions> options,
        TimeProvider timeProvider,
        ILogger<WalletTopUpSweepJob> logger)
    {
        _repository = repository;
        _service = service;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var pending = await _repository.ListPendingBetweenAsync(
            newerThanUtc: now.AddDays(-_options.ReconcileUpToDays),
            olderThanUtc: now.AddMinutes(-_options.ReconcileAfterMinutes),
            BatchSize);

        int resolved = 0;
        foreach (var topUp in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await _service.ReconcileAsync(topUp.Id, cancellationToken))
                {
                    resolved++;
                }
            }
            catch (Exception ex)
            {
                // One top-up's failure (a serialization conflict, a gateway hiccup) must not stop the rest; it stays
                // Pending and the next run looks at it again.
                _logger.LogError(ex, "Wallet top-up reconciliation failed for top-up {TopUpId}.", topUp.Id);
            }
        }

        _logger.LogInformation("Wallet top-up reconciliation: {Resolved} of {Checked} pending top-up(s) resolved.", resolved, pending.Count);
    }
}
