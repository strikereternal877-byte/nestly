using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.ProviderManagement;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Payment order creation, idempotency/dedup, and retry (SRS 11.11, 30.1,
/// tasks 68a-d, 70). A booking has at most one <see cref="PaymentTransaction"/>
/// ever (SRS 11.11.3 "booking-payment mapping shall be preserved") - both a
/// duplicate request and a retry after failure resolve to the same
/// transaction, just a different attempt underneath it.
/// </summary>
public class PaymentService : IPaymentService
{
    private const string Currency = "INR";

    private readonly IPaymentTransactionRepository _paymentRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IPaymentGateway _gateway;
    private readonly ISandboxPaymentSimulator _simulator;
    private readonly IPaymentWebhookService _webhookService;
    private readonly IEligibleProviderSearchService _eligibleProviderSearchService;

    public PaymentService(
        IPaymentTransactionRepository paymentRepository,
        IBookingRepository bookingRepository,
        IPaymentGateway gateway,
        ISandboxPaymentSimulator simulator,
        IPaymentWebhookService webhookService,
        IEligibleProviderSearchService eligibleProviderSearchService)
    {
        _paymentRepository = paymentRepository;
        _bookingRepository = bookingRepository;
        _gateway = gateway;
        _simulator = simulator;
        _webhookService = webhookService;
        _eligibleProviderSearchService = eligibleProviderSearchService;
    }

    public async Task<Result<PaymentOrderResponse>> CreateOrderAsync(Guid customerId, CreatePaymentOrderRequest request)
    {
        var booking = await _bookingRepository.GetByIdAsync(request.BookingId);
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Payment.BookingNotFound", "The specified booking does not exist.");
        }

        var existing = await _paymentRepository.GetByBookingIdAsync(booking.Id);

        // Checked before the booking-status gate below (task 70/duplicate-order
        // fix): a booking only ever reaches Confirmed via a successful payment
        // (PaymentWebhookService flips both in the same transaction), so
        // existing.Status == Success and booking.Status == Confirmed are
        // always true together. Gating on booking status first would catch
        // this case with the generic "not payable right now" message and make
        // this specific, friendlier AlreadyPaid branch unreachable - exactly
        // the confusing response a customer hits retrying "Pay" on a tab that
        // already succeeded (e.g. after a webhook confirmed it out of band).
        if (existing?.Status == PaymentTransactionStatus.Success)
        {
            return Error.Conflict("Payment.AlreadyPaid", "This booking has already been paid for.");
        }

        if (booking.Status is not (BookingStatus.PaymentPending or BookingStatus.PaymentFailed))
        {
            return Error.Business(
                "Payment.BookingNotPayable",
                $"Booking is in status '{booking.Status}' and cannot accept a payment right now.");
        }

        switch (existing?.Status)
        {
            case PaymentTransactionStatus.Pending:
                // Idempotency/dedup (task 68d): an attempt is already in
                // flight for this booking - hand back that same order rather
                // than creating a second one from a duplicate request.
                return Result.Success(ToOrderResponse(existing, existing.LatestAttempt!));

            case PaymentTransactionStatus.Cancelled:
                return Error.Business("Payment.TransactionCancelled", "This booking's payment was cancelled and can no longer be retried.");
        }

        // Gate payment on real fulfillability, not just slot capacity: a slot
        // window can have open capacity while zero providers can actually
        // take the job (skill, service area, availability, standing load and
        // travel feasibility all have to line up - see
        // ProviderAssignmentEligibilityService). Charging for a booking that
        // is unassignable on arrival would otherwise only surface as a
        // problem once an admin's manual queue - or auto-assignment - later
        // finds nobody, well after the customer has paid. Checked here,
        // immediately before minting a gateway order, rather than earlier at
        // booking creation: eligibility (especially travel feasibility) can
        // shift in the time between browsing a slot and reaching payment, and
        // this is the last moment before money is actually at stake.
        if (!await HasEligibleProviderAsync(booking.Id))
        {
            return Error.Business(
                "Payment.NoProviderAvailable",
                "No service professional is currently available for this date and time. Please choose a different slot.");
        }

        var gatewayResult = await _gateway.CreateOrderAsync(
            new GatewayCreateOrderRequest(booking.Id, booking.TotalPayableSnapshot, Currency, booking.Id.ToString("N")));

        PaymentTransaction transaction;
        if (existing is null)
        {
            transaction = new PaymentTransaction(
                Guid.NewGuid(), booking.Id, customerId, booking.TotalPayableSnapshot, Currency,
                string.IsNullOrWhiteSpace(request.IdempotencyKey) ? Guid.NewGuid().ToString("N") : request.IdempotencyKey);
            transaction.StartAttempt(Guid.NewGuid(), gatewayResult.GatewayOrderId);

            // task 135b: the read above and this insert are two separate
            // round trips, so two concurrent duplicate requests for the same
            // booking (a double-click, a client retry-on-timeout, two open
            // tabs) can both observe "no existing transaction" and both
            // reach here. TryAddAsync's unique-index guard lets only one
            // actually win; the loser falls back to that winner's
            // transaction and responds idempotently, exactly like the
            // non-concurrent duplicate-request path below already does.
            if (!await _paymentRepository.TryAddAsync(transaction))
            {
                var winner = await _paymentRepository.GetByBookingIdAsync(booking.Id);
                if (winner is null)
                {
                    // Pathological: the unique constraint fired but no row is
                    // visible under it yet. Surface this rather than
                    // fabricate a response - something is wrong beyond a
                    // simple lost race (e.g. read-your-own-writes isn't
                    // holding on the underlying store).
                    return Error.Infrastructure(
                        "Payment.OrderCreationRaceUnresolved",
                        "Could not create or locate a payment order for this booking. Please retry.");
                }

                return Result.Success(ToOrderResponse(winner, winner.LatestAttempt!));
            }
        }
        else
        {
            // Retry (task 70): existing.Status is Failed here - reuse the
            // same transaction (preserving the booking-payment mapping and
            // the customer's original booking intent) and just add a new
            // attempt underneath it. The booking itself moves back to
            // PaymentPending so it is, once again, "awaiting payment" - and
            // so the webhook handler's eventual Confirmed/PaymentFailed
            // transition has a valid PaymentPending state to move from
            // (BookingLifecycle has no direct PaymentFailed -> Confirmed edge).
            transaction = existing;
            transaction.StartAttempt(Guid.NewGuid(), gatewayResult.GatewayOrderId);
            await _paymentRepository.UpdateAsync(transaction);

            if (booking.Status == BookingStatus.PaymentFailed)
            {
                booking.TransitionTo(BookingStatus.PaymentPending, "Retrying payment.");
                await _bookingRepository.UpdateAsync(booking);
            }
        }

        return Result.Success(ToOrderResponse(transaction, transaction.LatestAttempt!));
    }

    /// <summary>
    /// Stops at the first candidate that passes the full eligibility gate
    /// (schedule conflict, availability, capacity, travel feasibility) rather
    /// than materialising the ranked list - existence is all this needs, and
    /// <see cref="IEligibleProviderSearchService"/> is lazy specifically so a
    /// caller that stops early never pays for the billed route lookups behind
    /// the candidates it didn't look at.
    /// </summary>
    private async Task<bool> HasEligibleProviderAsync(Guid bookingId)
    {
        await foreach (var _ in _eligibleProviderSearchService.FindEligibleAsync(bookingId))
        {
            return true;
        }

        return false;
    }

    public async Task<Result> SimulateAsync(Guid customerId, SimulatePaymentRequest request)
    {
        var transaction = await _paymentRepository.GetByGatewayOrderIdAsync(request.GatewayOrderId);
        if (transaction is null || transaction.CustomerId != customerId)
        {
            return Result.Failure(Error.NotFound("Payment.OrderNotFound", "No payment attempt exists for this gateway order."));
        }

        var outcome = _simulator.DetermineOutcome(transaction.Amount);
        string status = outcome.Succeeded ? PaymentWebhookPayload.SuccessStatus : PaymentWebhookPayload.FailedStatus;
        // Even a declined sandbox attempt gets a reference - a real gateway
        // typically assigns one to a failed attempt too, and the webhook's
        // validator/signature both require a non-empty value here.
        string gatewayPaymentRef = outcome.Succeeded ? outcome.GatewayPaymentRef : $"sandbox_declined_{Guid.NewGuid():N}";

        string canonicalPayload = PaymentWebhookPayload.Build(request.GatewayOrderId, gatewayPaymentRef, status);
        string signature = _gateway.SignPayload(canonicalPayload);

        return await _webhookService.HandleCallbackAsync(new PaymentWebhookRequest(request.GatewayOrderId, gatewayPaymentRef, status, signature));
    }

    public async Task<Result<PaymentTransactionResponse>> GetByBookingIdAsync(Guid customerId, Guid bookingId)
    {
        var transaction = await _paymentRepository.GetByBookingIdAsync(bookingId);
        if (transaction is null || transaction.CustomerId != customerId)
        {
            return Error.NotFound("Payment.NotFound", "No payment transaction exists for this booking.");
        }

        return Result.Success(ToTransactionResponse(transaction));
    }

    private static PaymentOrderResponse ToOrderResponse(PaymentTransaction transaction, PaymentAttempt attempt) => new(
        transaction.Id, attempt.Id, attempt.GatewayOrderId, transaction.Amount, transaction.Currency, attempt.AttemptNumber, attempt.CreatedAtUtc);

    private static PaymentTransactionResponse ToTransactionResponse(PaymentTransaction transaction) => new(
        transaction.Id,
        transaction.BookingId,
        transaction.CustomerId,
        transaction.Amount,
        transaction.Currency,
        transaction.Status,
        transaction.Attempts
            .OrderBy(a => a.AttemptNumber)
            .Select(a => new PaymentAttemptResponse(a.Id, a.AttemptNumber, a.GatewayOrderId, a.GatewayPaymentRef, a.Status, a.FailureReason, a.CreatedAtUtc, a.CompletedAtUtc))
            .ToList(),
        transaction.CreatedAtUtc,
        transaction.UpdatedAtUtc,
        transaction.CommissionRatePercentage,
        transaction.CommissionAmount);
}
