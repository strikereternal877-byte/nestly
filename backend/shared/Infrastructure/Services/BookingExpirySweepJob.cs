using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application.Bookings;
using Nestly.Domain;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>See <see cref="IBookingExpirySweepJob"/>.</summary>
public class BookingExpirySweepJob : IBookingExpirySweepJob
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IUnpaidBookingReleaseService _releaseService;
    private readonly IOptions<BookingExpiryOptions> _options;
    private readonly IOptions<RecurringBookingOptions> _recurringOptions;
    private readonly ILogger<BookingExpirySweepJob> _logger;

    public BookingExpirySweepJob(
        IBookingRepository bookingRepository,
        IUnpaidBookingReleaseService releaseService,
        IOptions<BookingExpiryOptions> options,
        IOptions<RecurringBookingOptions> recurringOptions,
        ILogger<BookingExpirySweepJob> logger)
    {
        _bookingRepository = bookingRepository;
        _releaseService = releaseService;
        _options = options;
        _recurringOptions = recurringOptions;
        _logger = logger;
    }

    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        var cutoffUtc = DateTime.UtcNow.AddMinutes(-_options.Value.ExpiryMinutes);
        // A recurring-generated occurrence gets its own, much longer, cutoff -
        // see RecurringBookingOptions.PaymentWindowHours's doc comment for why
        // the one-off checkout window is the wrong clock for a booking nobody
        // is actively watching.
        var recurringCutoffUtc = DateTime.UtcNow.AddHours(-_recurringOptions.Value.PaymentWindowHours);
        var stale = await _bookingRepository.ListStalePaymentPendingAsync(cutoffUtc, recurringCutoffUtc);

        foreach (var booking in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _releaseService.ExpireAsync(booking, "Payment was not completed within the expiry window.");
        }

        _logger.LogInformation("Booking expiry sweep: {ExpiredCount} stale PaymentPending booking(s) expired.", stale.Count);
    }
}
