using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nestly.Application.Wallet;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Runs <see cref="IWalletCreditExpirySweepJob"/> on a recurring schedule
/// (task 175 - "the sweep job must not be built before that [FIFO]
/// allocation model is designed", REFERRAL.md "FUTURE ENHANCEMENTS"). The
/// FIFO consumption-tracking design and the sweep's own logic
/// (<see cref="WalletCreditExpirySweepJob"/>) were built first and are unit
/// tested independently (see <c>WalletCreditExpiryTests</c>) - this class is
/// only the "run it once a day" trigger.
///
/// This codebase has no job-scheduling framework anywhere yet (no Hangfire,
/// no other <see cref="BackgroundService"/>) - introducing one is a bigger
/// architectural decision than a single daily sweep warrants, so this uses
/// only the standard, dependency-free .NET hosting primitive
/// (<see cref="BackgroundService"/> + <see cref="PeriodicTimer"/>) rather
/// than adding a new package. A future job with tighter timing/retry/
/// distributed-lock needs should prompt evaluating a real scheduler then,
/// not before there is a second job that needs one.
///
/// Registered once, in consumer-api's composition root only (not inside
/// <c>AddInfrastructure</c>, which every API process shares) - admin-api and
/// partner-api must not each run their own copy of the same sweep.
/// </summary>
public sealed class WalletCreditExpirySweepHostedService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WalletCreditExpirySweepHostedService> _logger;

    public WalletCreditExpirySweepHostedService(IServiceScopeFactory scopeFactory, ILogger<WalletCreditExpirySweepHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        // Run once at startup too, rather than waiting a full interval for
        // the first sweep - an expiring credit that lapsed while the process
        // was down should not wait up to 24h more to be written off.
        await RunSweepAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSweepAsync(stoppingToken);
        }
    }

    private async Task RunSweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<IWalletCreditExpirySweepJob>();
            await job.SweepAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down - not a sweep failure.
        }
        catch (Exception ex)
        {
            // Never let one bad sweep run crash the host - the next
            // scheduled tick tries again, same "one inconsistent row never
            // breaks the whole sweep" defensiveness WalletService.ExpireCreditAsync
            // already applies at the row level.
            _logger.LogError(ex, "Wallet credit expiry sweep run failed; will retry on the next scheduled tick.");
        }
    }
}
