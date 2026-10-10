using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Nestly.Application;
using Nestly.Application.Abstractions.Observability;
using Nestly.Application.Bookings;
using Nestly.Application.Escrow;
using Nestly.Application.Payments;
using Nestly.Application.RecurringBookings;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Observability;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Payment callback handling (SRS 30.1, 11.11.3, tasks 69a-c): verifies the
/// gateway's signature, applies the outcome idempotently, and keeps the
/// booking-payment mapping in sync. Depends on <see cref="NestlyDbContext"/>
/// directly (unlike most Infrastructure services, which only see it through
/// a repository) because this is the one payment operation that must commit
/// two different aggregates - the PaymentTransaction and the Booking - as a
/// single atomic unit: a crash between the two SaveChanges calls a plain
/// repository-per-aggregate approach would make must never leave a booking
/// Confirmed with no successful payment recorded, or vice versa.
///
/// A successful callback also settles two more things in the same
/// transaction (tasks 157-158): the platform's commission on this booking is
/// computed and recorded on the transaction, and the paid amount moves into
/// the platform escrow ledger, where it stays held until the booking is
/// completed or refunded.
/// </summary>
public class PaymentWebhookService : IPaymentWebhookService
{
    private readonly IPaymentTransactionRepository _paymentRepository;
    private readonly IPaymentGroupRepository _groupRepository;
    private readonly IRecurringBookingPlanRepository _planRepository;
    private readonly IBookingRepository _bookingRepository;
    private readonly IServiceRepository _serviceRepository;
    private readonly IPaymentGateway _gateway;
    private readonly ICommissionService _commissionService;
    private readonly IEscrowService _escrowService;
    private readonly NestlyDbContext _context;
    private readonly IMetricsService _metricsService;
    private readonly ILogger<PaymentWebhookService> _logger;

    public PaymentWebhookService(
        IPaymentTransactionRepository paymentRepository,
        IPaymentGroupRepository groupRepository,
        IRecurringBookingPlanRepository planRepository,
        IBookingRepository bookingRepository,
        IServiceRepository serviceRepository,
        IPaymentGateway gateway,
        ICommissionService commissionService,
        IEscrowService escrowService,
        NestlyDbContext context,
        IMetricsService metricsService,
        ILogger<PaymentWebhookService> logger)
    {
        _paymentRepository = paymentRepository;
        _groupRepository = groupRepository;
        _planRepository = planRepository;
        _bookingRepository = bookingRepository;
        _serviceRepository = serviceRepository;
        _gateway = gateway;
        _commissionService = commissionService;
        _escrowService = escrowService;
        _context = context;
        _metricsService = metricsService;
        _logger = logger;
    }

    public async Task<Result> HandleCallbackAsync(PaymentWebhookRequest request)
    {
        string canonicalPayload = _gateway.BuildCanonicalPayload(request);
        if (!_gateway.VerifyWebhookSignature(canonicalPayload, request.Signature))
        {
            // SRS 28.3 "payment callback abuse" - an unsigned/mis-signed
            // callback never touches any state below this point.
            // The order id is unverified caller input at this point, so it is sanitised before it reaches the log.
            _logger.LogWarning(
                "Rejected a payment webhook with an invalid signature for gateway order {GatewayOrderId}.",
                LogSanitizer.ForLog(request.GatewayOrderId));
            return Result.Failure(Error.Unauthorized("Payment.InvalidWebhookSignature", "The webhook signature could not be verified."));
        }

        var transaction = await _paymentRepository.GetByGatewayOrderIdAsync(request.GatewayOrderId);
        if (transaction is null)
        {
            // A prepaid checkout's order id belongs to its PaymentGroup: the gateway knows
            // one order, this system settles several bookings against it.
            var group = await _groupRepository.GetByGatewayOrderIdAsync(request.GatewayOrderId);
            if (group is null)
            {
                return Result.Failure(Error.NotFound("Payment.OrderNotFound", "No payment attempt exists for this gateway order."));
            }

            if (group.Status != PaymentGroupStatus.Pending)
            {
                // Same idempotent duplicate handling as a single attempt: the first resolution wins.
                _logger.LogInformation(
                    "Ignored a duplicate payment webhook for gateway order {GatewayOrderId} (payment group already {Status}).",
                    LogSanitizer.ForLog(group.GatewayOrderId), group.Status);
                return Result.Success();
            }

            bool groupSucceeded = string.Equals(request.Status, PaymentWebhookPayload.SuccessStatus, StringComparison.OrdinalIgnoreCase);
            await ResolveGroupAsync(group, groupSucceeded, request.Status, request.GatewayPaymentRef);
            return Result.Success();
        }

        var attempt = transaction.Attempts.Single(a => a.GatewayOrderId == request.GatewayOrderId);

        if (attempt.Status != PaymentAttemptStatus.Created)
        {
            // Idempotent duplicate handling (task 69b): this attempt was
            // already resolved by an earlier callback (the gateway is free
            // to redeliver). The first resolution always wins - a duplicate
            // is a no-op success, never re-applied, even if the redelivery
            // reports a different outcome than the first one did.
            //
            // This is only a fast-path short-circuit against the snapshot we
            // just read, not the actual guard - two concurrent redeliveries
            // can both load Created before either commits. The real,
            // race-proof guard is the conditional ExecuteUpdateAsync inside
            // ResolveAttemptAsync (NESTLY-006).
            _logger.LogInformation(
                "Ignored a duplicate payment webhook for gateway order {GatewayOrderId} (attempt already {Status}).",
                LogSanitizer.ForLog(attempt.GatewayOrderId), attempt.Status);
            return Result.Success();
        }

        var booking = await _bookingRepository.GetByIdAsync(transaction.BookingId);
        if (booking is null)
        {
            // Data-integrity failure, not a business outcome - the FK from
            // payment_transaction to booking is Restrict, so this should be
            // unreachable in practice.
            throw new InvalidOperationException($"Booking {transaction.BookingId} referenced by payment transaction {transaction.Id} was not found.");
        }

        bool succeeded = string.Equals(request.Status, PaymentWebhookPayload.SuccessStatus, StringComparison.OrdinalIgnoreCase);
        await ResolveAttemptAsync(transaction, attempt, booking, succeeded, request.Status, request.GatewayPaymentRef);
        return Result.Success();
    }

    /// <summary>
    /// The customer-triggered counterpart to <see cref="HandleCallbackAsync"/>
    /// (SRS 30.1's webhook path is not the only way an outcome can reach
    /// this system - see <see cref="IPaymentWebhookService.VerifyPendingAttemptAsync"/>'s
    /// own doc comment for why). Asks the gateway directly rather than
    /// waiting on a callback that, for an abandoned/cancelled checkout, may
    /// never come.
    /// </summary>
    public async Task<Result<PaymentTransaction>> VerifyPendingAttemptAsync(Guid bookingId)
    {
        var transaction = await _paymentRepository.GetByBookingIdAsync(bookingId);
        if (transaction is null)
        {
            return Error.NotFound("Payment.NotFound", "No payment transaction exists for this booking.");
        }

        var attempt = transaction.LatestAttempt;
        if (attempt is null || attempt.Status != PaymentAttemptStatus.Created)
        {
            // Already resolved (by a webhook that arrived in the meantime,
            // or a prior call to this same method) or never started -
            // nothing new to verify. Same idempotent-no-op shape as
            // HandleCallbackAsync's duplicate-delivery branch.
            return Result.Success(transaction);
        }

        if (attempt.PaymentGroupId is { } groupId)
        {
            // Part of a prepaid checkout: the gateway is asked about the group's order, and the
            // outcome is applied to every member together.
            return await VerifyPendingGroupAsync(transaction, groupId);
        }

        var verifyResult = await _gateway.VerifyOrderStatusAsync(attempt.GatewayOrderId);
        if (string.Equals(verifyResult.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            // The gateway itself hasn't reached an outcome yet (e.g. a
            // netbanking mode with delayed settlement) - leave it alone
            // rather than force a premature failure. The customer's polling
            // (or a later webhook) will pick up the eventual real outcome.
            return Result.Success(transaction);
        }

        var booking = await _bookingRepository.GetByIdAsync(transaction.BookingId);
        if (booking is null)
        {
            throw new InvalidOperationException($"Booking {transaction.BookingId} referenced by payment transaction {transaction.Id} was not found.");
        }

        bool succeeded = string.Equals(verifyResult.Status, PaymentWebhookPayload.SuccessStatus, StringComparison.OrdinalIgnoreCase);
        string status = succeeded ? PaymentWebhookPayload.SuccessStatus : (verifyResult.FailureReason ?? "Payment was not completed.");
        await ResolveAttemptAsync(transaction, attempt, booking, succeeded, status, verifyResult.GatewayPaymentRef);
        return Result.Success(transaction);
    }

    private async Task<Result<PaymentTransaction>> VerifyPendingGroupAsync(PaymentTransaction transaction, Guid groupId)
    {
        var group = await _groupRepository.GetByIdAsync(groupId);
        if (group is null || group.Status != PaymentGroupStatus.Pending)
        {
            return Result.Success(transaction);
        }

        var verifyResult = await _gateway.VerifyOrderStatusAsync(group.GatewayOrderId);
        if (string.Equals(verifyResult.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success(transaction);
        }

        bool succeeded = string.Equals(verifyResult.Status, PaymentWebhookPayload.SuccessStatus, StringComparison.OrdinalIgnoreCase);
        string status = succeeded ? PaymentWebhookPayload.SuccessStatus : (verifyResult.FailureReason ?? "Payment was not completed.");
        await ResolveGroupAsync(group, succeeded, status, verifyResult.GatewayPaymentRef);

        // The caller reads the lead's transaction back; reload so it reflects what was just applied.
        return Result.Success(await _paymentRepository.GetByIdAsync(transaction.Id) ?? transaction);
    }

    /// <summary>
    /// <see cref="ResolveAttemptAsync"/> for a prepaid checkout: applies one gateway
    /// outcome to every booking the group covers, in a single database transaction, so
    /// a payment can never leave some visits Confirmed and others still awaiting the
    /// same money. The race guard is the group's own conditional Pending flip - of two
    /// concurrent deliveries only one gets to apply anything.
    ///
    /// <para>
    /// Everything downstream stays per booking: each member gets its own commission
    /// and escrow hold from <see cref="ApplySuccessfulPaymentAsync"/>, which is what
    /// keeps refunds and provider payouts per visit. On success the plan's pending
    /// cycle is also released, so the daily jobs treat the cycle as paid.
    /// </para>
    /// </summary>
    private async Task ResolveGroupAsync(PaymentGroup group, bool succeeded, string status, string? gatewayPaymentRef)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var dbTransaction = await _context.Database.BeginTransactionAsync();
        try
        {
            bool wonRace = await _groupRepository.TryMarkResolvedAsync(
                group.Id, succeeded ? PaymentGroupStatus.Success : PaymentGroupStatus.Failed);

            if (!wonRace)
            {
                await dbTransaction.CommitAsync();
                _logger.LogInformation(
                    "Ignored a payment resolution for gateway order {GatewayOrderId} (payment group was already resolved by a concurrent delivery).",
                    group.GatewayOrderId);
                return;
            }

            var members = await _groupRepository.ListMemberTransactionsAsync(group.Id);
            foreach (var transaction in members)
            {
                var attempt = transaction.Attempts.Single(a => a.PaymentGroupId == group.Id);
                var booking = await _bookingRepository.GetByIdAsync(transaction.BookingId)
                    ?? throw new InvalidOperationException($"Booking {transaction.BookingId} referenced by payment transaction {transaction.Id} was not found.");

                if (!await _paymentRepository.TryMarkAttemptResolvedAsync(
                        attempt.Id, succeeded ? PaymentAttemptStatus.Success : PaymentAttemptStatus.Failed))
                {
                    continue;
                }

                if (succeeded)
                {
                    transaction.MarkAttemptSucceeded(attempt.Id, gatewayPaymentRef ?? string.Empty);
                    await ApplySuccessfulPaymentAsync(transaction, booking, "Payment succeeded.");
                }
                else
                {
                    transaction.MarkAttemptFailed(attempt.Id, status);
                    booking.TransitionTo(BookingStatus.PaymentFailed, "Payment failed.");
                    await _paymentRepository.UpdateAsync(transaction);
                    await _bookingRepository.UpdateAsync(booking);
                }
            }

            if (succeeded)
            {
                group.MarkSucceeded(gatewayPaymentRef ?? string.Empty);
                await _groupRepository.UpdateAsync(group);

                var plan = await _planRepository.GetByPendingPrepaymentLeadAsync(group.LeadBookingId);
                if (plan is not null)
                {
                    plan.ConfirmPrepayment();
                    await _planRepository.UpdateAsync(plan);
                }
            }

            await dbTransaction.CommitAsync();
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }

        stopwatch.Stop();
        _metricsService.RecordPaymentOutcome(succeeded, stopwatch.Elapsed, succeeded ? null : status);
    }

    /// <summary>
    /// The actual, race-proof resolution shared by every way a payment
    /// outcome can reach this system (a real webhook, a redelivered one, and
    /// now an active gateway verify) - applying the outcome to the
    /// transaction/attempt and, on success, the booking/commission/escrow.
    /// The caller has already confirmed <paramref name="attempt"/> still
    /// looks unresolved; this method's own conditional update is what makes
    /// that safe against a second, concurrent caller observing the same
    /// stale snapshot.
    /// </summary>
    private async Task ResolveAttemptAsync(
        PaymentTransaction transaction, PaymentAttempt attempt, Booking booking,
        bool succeeded, string status, string? gatewayPaymentRef)
    {
        // Task 137a: measures the whole resolution path, including the DB
        // transaction commit - the latency a real payment failure/success
        // takes to become durable. Not started until this point (an invalid
        // signature or an already-resolved duplicate is not a new payment
        // outcome, so neither should skew the metric).
        var stopwatch = Stopwatch.StartNew();

        await using var dbTransaction = await _context.Database.BeginTransactionAsync();
        try
        {
            // NESTLY-006: the actual idempotency guard. A single conditional
            // UPDATE that only ever affects a row still in Created - the same
            // pattern SlotCapacityRepository.TryReserveAsync uses to close a
            // capacity race. Of two concurrent resolutions for the same
            // attempt (a redelivered webhook racing this same active-verify
            // path, or two of either), only one can ever flip it; the loser
            // affects zero rows and must bail out here, before doing
            // anything else, so it never re-applies the booking transition or
            // the escrow hold a second time.
            bool wonRace = await _paymentRepository.TryMarkAttemptResolvedAsync(
                attempt.Id, succeeded ? PaymentAttemptStatus.Success : PaymentAttemptStatus.Failed);

            if (!wonRace)
            {
                await dbTransaction.CommitAsync();
                _logger.LogInformation(
                    "Ignored a payment resolution for gateway order {GatewayOrderId} (attempt was already resolved by a concurrent delivery).",
                    attempt.GatewayOrderId);
                return;
            }

            if (succeeded)
            {
                transaction.MarkAttemptSucceeded(attempt.Id, gatewayPaymentRef ?? string.Empty);
                await ApplySuccessfulPaymentAsync(transaction, booking, "Payment succeeded.");
            }
            else
            {
                transaction.MarkAttemptFailed(attempt.Id, status);
                booking.TransitionTo(BookingStatus.PaymentFailed, "Payment failed.");

                await _paymentRepository.UpdateAsync(transaction);
                await _bookingRepository.UpdateAsync(booking);
            }

            await dbTransaction.CommitAsync();
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }

        stopwatch.Stop();
        _metricsService.RecordPaymentOutcome(succeeded, stopwatch.Elapsed, succeeded ? null : status);
    }

    /// <summary>
    /// Row 25 (docs/OPEN-FIXES-FEATURES.csv): lets an admin record a
    /// manual/offline payment (cash, UPI, bank transfer) against a booking
    /// still Awaiting Payment, and moves it forward exactly the same way a
    /// successful gateway callback does - <see cref="ApplySuccessfulPaymentAsync"/>
    /// is the same commission/escrow/Confirmed-transition code
    /// <see cref="HandleCallbackAsync"/> runs on a real gateway success, so
    /// this never re-derives that policy. There is no gateway order for a
    /// manual payment, so a synthetic one is minted for the attempt row, and
    /// the admin-supplied method/reference is folded into
    /// <see cref="PaymentAttempt.GatewayPaymentRef"/> - the same field a real
    /// gateway populates - rather than adding gateway-shaped columns for a
    /// channel that has none.
    /// </summary>
    public async Task<Result<PaymentTransaction>> RecordManualPaymentAsync(Guid bookingId, ManualPaymentMethod method, string reference)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("Booking.NotFound", "The specified booking does not exist.");
        }

        // Same "payable right now" gate PaymentService.CreateOrderAsync uses
        // for a gateway order - Awaiting Payment (PaymentPending) or a
        // previously failed attempt (PaymentFailed) are the only states a
        // payment, manual or gateway, can still be recorded against.
        if (booking.Status is not (BookingStatus.PaymentPending or BookingStatus.PaymentFailed))
        {
            return Error.Business(
                "Payment.BookingNotPayable",
                $"Booking is in status '{booking.Status}' and cannot accept a payment right now.");
        }

        var transaction = await _paymentRepository.GetByBookingIdAsync(bookingId);
        if (transaction?.Status == PaymentTransactionStatus.Success)
        {
            return Error.Conflict("Payment.AlreadyPaid", "This booking has already been paid for.");
        }

        string manualGatewayOrderId = $"manual-{Guid.NewGuid():N}";
        string gatewayPaymentRef = $"manual:{method}:{reference}";

        await using var dbTransaction = await _context.Database.BeginTransactionAsync();
        try
        {
            PaymentAttempt attempt;
            if (transaction is null)
            {
                transaction = new PaymentTransaction(
                    Guid.NewGuid(), booking.Id, booking.CustomerId, booking.TotalPayableSnapshot, "INR", Guid.NewGuid().ToString("N"));
                attempt = transaction.StartAttempt(Guid.NewGuid(), manualGatewayOrderId);
                if (!await _paymentRepository.TryAddAsync(transaction))
                {
                    await dbTransaction.RollbackAsync();
                    return Error.Infrastructure(
                        "Payment.OrderCreationRaceUnresolved", "Could not create a payment record for this booking. Please retry.");
                }
            }
            else
            {
                attempt = transaction.StartAttempt(Guid.NewGuid(), manualGatewayOrderId);
            }

            transaction.MarkAttemptSucceeded(attempt.Id, gatewayPaymentRef);
            await ApplySuccessfulPaymentAsync(transaction, booking, "Manual payment recorded by admin.");

            await dbTransaction.CommitAsync();
        }
        catch
        {
            await dbTransaction.RollbackAsync();
            throw;
        }

        return Result.Success(transaction);
    }

    /// <summary>
    /// The part of a successful payment that is identical whether the money
    /// arrived via a gateway callback (<see cref="HandleCallbackAsync"/>) or
    /// a manual admin entry (<see cref="RecordManualPaymentAsync"/>): commit
    /// commission (task 157), move the booking to Confirmed via the one
    /// state-machine entry point (<see cref="Booking.TransitionTo"/>), and
    /// hold the amount in escrow (task 158). Caller is responsible for
    /// having already called <see cref="PaymentTransaction.MarkAttemptSucceeded"/>
    /// and for the surrounding DB transaction.
    /// </summary>
    private async Task ApplySuccessfulPaymentAsync(PaymentTransaction transaction, Booking booking, string transitionReason)
    {
        booking.TransitionTo(BookingStatus.Confirmed, transitionReason);

        Guid? categoryId = null;
        var firstItem = booking.Items.FirstOrDefault();
        if (firstItem is not null)
        {
            var service = await _serviceRepository.GetByIdAsync(firstItem.ServiceId);
            categoryId = service?.CategoryId;
        }

        var commission = _commissionService.Calculate(transaction.Amount, categoryId);
        transaction.RecordCommission(commission.RatePercentage, commission.CommissionAmount);

        await _paymentRepository.UpdateAsync(transaction);
        await _bookingRepository.UpdateAsync(booking);

        await _escrowService.HoldAsync(booking.Id, transaction.Id, transaction.Amount);
    }
}
