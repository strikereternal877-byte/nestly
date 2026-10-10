using Nestly.Application.Bookings;
using Nestly.Application.Payments;
using Nestly.Application.ProviderManagement;
using Nestly.Application.RecurringBookings;
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
    private readonly IPaymentGroupRepository _groupRepository;
    private readonly IRecurringBookingPlanRepository _planRepository;
    private readonly IRecurringBookingOccurrenceRepository _occurrenceRepository;
    private readonly IUnpaidBookingReleaseService _releaseService;

    public PaymentService(
        IPaymentTransactionRepository paymentRepository,
        IBookingRepository bookingRepository,
        IPaymentGateway gateway,
        ISandboxPaymentSimulator simulator,
        IPaymentWebhookService webhookService,
        IEligibleProviderSearchService eligibleProviderSearchService,
        IPaymentGroupRepository groupRepository,
        IRecurringBookingPlanRepository planRepository,
        IRecurringBookingOccurrenceRepository occurrenceRepository,
        IUnpaidBookingReleaseService releaseService)
    {
        _paymentRepository = paymentRepository;
        _bookingRepository = bookingRepository;
        _gateway = gateway;
        _simulator = simulator;
        _webhookService = webhookService;
        _eligibleProviderSearchService = eligibleProviderSearchService;
        _groupRepository = groupRepository;
        _planRepository = planRepository;
        _occurrenceRepository = occurrenceRepository;
        _releaseService = releaseService;
    }

    public async Task<Result<PaymentOrderResponse>> CreateOrderAsync(Guid customerId, CreatePaymentOrderRequest request)
    {
        var booking = await _bookingRepository.GetByIdAsync(request.BookingId);
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Payment.BookingNotFound", "The specified booking does not exist.");
        }

        // A prepaid plan's cycle is paid in one checkout keyed by its lead booking;
        // every other booking takes the ordinary one-booking path below.
        var prepaidPlan = await _planRepository.GetByPendingPrepaymentLeadAsync(booking.Id);
        if (prepaidPlan is not null)
        {
            return await CreatePrepaidGroupOrderAsync(customerId, booking, prepaidPlan);
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
                // than creating a second one from a duplicate request. A
                // hosted-checkout gateway's redirect form still needs
                // rebuilding here (e.g. a customer who navigated away and
                // came back): RebuildCheckoutAsync reuses the attempt's own
                // GatewayOrderId rather than minting a new one, so the
                // webhook's eventual lookup by that id still resolves.
                return Result.Success(await ToOrderResponseAsync(booking, existing, existing.LatestAttempt!));

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
            new GatewayCreateOrderRequest(
                booking.Id, booking.TotalPayableSnapshot, Currency, booking.Id.ToString("N"),
                CustomerName: booking.CustomerNameSnapshot, CustomerMobile: booking.CustomerMobileSnapshot));

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

                // This request lost the race, so gatewayResult above belongs
                // to an order nobody persisted - rebuild the checkout form
                // against the winner's actual attempt instead of handing
                // back a redirect for an order id no PaymentAttempt row
                // references.
                return Result.Success(await ToOrderResponseAsync(booking, winner, winner.LatestAttempt!));
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

        // Both the fresh-transaction and retry branches above already
        // computed gatewayResult against the attempt they just started, so
        // its redirect fields (if any) are used directly rather than
        // rebuilding via another gateway call.
        return Result.Success(ToOrderResponse(transaction, transaction.LatestAttempt!, gatewayResult));
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
        // The one real enforcement of this method's own "sandbox-only"
        // contract (see its XML doc comment) - until this existed, nothing
        // stopped an authenticated customer from calling this endpoint
        // directly against their own real PayU order and having it marked
        // paid via ISandboxPaymentSimulator's fake deterministic outcome,
        // with no card ever actually charged. ISandboxPaymentSimulator is
        // always bound to the concrete sandbox regardless of which
        // IPaymentGateway is active (PaymentGatewayRegistration's own doc
        // comment), so a type check against the real gateway instance is
        // what actually closes this - checking configuration again here
        // would just re-derive the same fact _gateway's concrete type
        // already encodes.
        if (_gateway is not SandboxPaymentGateway)
        {
            return Result.Failure(Error.Business(
                "Payment.SimulateNotAvailable",
                "Payment simulation is not available: a real payment gateway is configured."));
        }

        // A prepaid checkout's order id belongs to its PaymentGroup, not to any one
        // member booking; the amount to simulate is then the whole group's total.
        decimal amountToSimulate;
        var transaction = await _paymentRepository.GetByGatewayOrderIdAsync(request.GatewayOrderId);
        if (transaction is not null)
        {
            if (transaction.CustomerId != customerId)
            {
                return Result.Failure(Error.NotFound("Payment.OrderNotFound", "No payment attempt exists for this gateway order."));
            }

            amountToSimulate = transaction.Amount;
        }
        else
        {
            var group = await _groupRepository.GetByGatewayOrderIdAsync(request.GatewayOrderId);
            if (group is null || group.CustomerId != customerId)
            {
                return Result.Failure(Error.NotFound("Payment.OrderNotFound", "No payment attempt exists for this gateway order."));
            }

            amountToSimulate = group.TotalAmount;
        }

        var outcome = _simulator.DetermineOutcome(amountToSimulate);
        string status = outcome.Succeeded ? PaymentWebhookPayload.SuccessStatus : PaymentWebhookPayload.FailedStatus;
        // Even a declined sandbox attempt gets a reference - a real gateway
        // typically assigns one to a failed attempt too, and the webhook's
        // validator/signature both require a non-empty value here.
        string gatewayPaymentRef = outcome.Succeeded ? outcome.GatewayPaymentRef : $"sandbox_declined_{Guid.NewGuid():N}";

        string canonicalPayload = PaymentWebhookPayload.Build(request.GatewayOrderId, gatewayPaymentRef, status);
        string signature = _simulator.SignPayload(canonicalPayload);

        return await _webhookService.HandleCallbackAsync(new PaymentWebhookRequest(request.GatewayOrderId, gatewayPaymentRef, status, signature));
    }

    public async Task<Result<PaymentTransactionResponse>> GetByBookingIdAsync(Guid customerId, Guid bookingId)
    {
        var transaction = await _paymentRepository.GetByBookingIdAsync(bookingId);
        if (transaction is null || transaction.CustomerId != customerId)
        {
            return Error.NotFound("Payment.NotFound", "No payment transaction exists for this booking.");
        }

        var response = ToTransactionResponse(transaction);

        // A visit of a prepaid plan was paid together with the others: say what the one payment came to.
        var groupId = transaction.Attempts
            .Where(a => a.PaymentGroupId != null && a.Status == PaymentAttemptStatus.Success)
            .Select(a => a.PaymentGroupId)
            .FirstOrDefault();
        if (groupId is { } id && await _groupRepository.GetByIdAsync(id) is { } group)
        {
            response = response with { PrepaidCheckoutTotal = group.TotalAmount, PrepaidCheckoutVisitCount = group.VisitCount };
        }

        return Result.Success(response);
    }

    public async Task<Result<PaymentTransactionResponse>> VerifyPendingAsync(Guid customerId, Guid bookingId)
    {
        // Ownership check first, against the repository directly - calling
        // IPaymentWebhookService.VerifyPendingAttemptAsync before confirming
        // the caller owns this booking would let any authenticated customer
        // trigger a gateway lookup (and a real state resolution) for anyone
        // else's payment.
        var existing = await _paymentRepository.GetByBookingIdAsync(bookingId);
        if (existing is null || existing.CustomerId != customerId)
        {
            return Error.NotFound("Payment.NotFound", "No payment transaction exists for this booking.");
        }

        var result = await _webhookService.VerifyPendingAttemptAsync(bookingId);
        return result.IsSuccess ? Result.Success(ToTransactionResponse(result.Value)) : result.Error;
    }

    /// <summary>
    /// The prepaid counterpart of the single-booking order: one gateway order
    /// that pays the plan's whole unpaid cycle - the lead booking plus every
    /// other booking the plan created for it. Each member keeps its own
    /// transaction and attempt (so commission, escrow, refunds and payouts stay
    /// per booking); the <see cref="PaymentGroup"/> is the single order the
    /// customer actually pays.
    ///
    /// <para>
    /// The provider-eligibility gate runs for every member, exactly as it does
    /// for a single booking: a visit nobody can serve is released and dropped
    /// from the purchase (the customer is shown the date and is not charged for
    /// it). Only the lead being unstaffable blocks the purchase, since it is the
    /// booking the customer is standing on.
    /// </para>
    /// </summary>
    private async Task<Result<PaymentOrderResponse>> CreatePrepaidGroupOrderAsync(Guid customerId, Booking lead, RecurringBookingPlan plan)
    {
        if (lead.Status is not (BookingStatus.PaymentPending or BookingStatus.PaymentFailed))
        {
            return Error.Business(
                "Payment.BookingNotPayable",
                $"Booking is in status '{lead.Status}' and cannot accept a payment right now.");
        }

        var planBookings = await _bookingRepository.ListByRecurringPlanAsync(plan.Id);

        // Idempotency: the payment page creates its order on load, so a reload, a second tab or a
        // client retry must hand back the order already in flight instead of minting another.
        var latest = await _groupRepository.GetLatestByLeadBookingIdAsync(lead.Id);
        if (latest is { Status: PaymentGroupStatus.Pending })
        {
            return Result.Success(await ToGroupOrderResponseAsync(lead, latest, plan, planBookings));
        }

        var candidates = new List<Booking> { lead };
        candidates.AddRange(planBookings
            .Where(b => b.Id != lead.Id && b.Status is BookingStatus.PaymentPending or BookingStatus.PaymentFailed)
            .OrderBy(b => b.SlotDate));

        var members = new List<Booking>();
        foreach (var candidate in candidates)
        {
            // Retry after a failed group: the member goes back to "awaiting payment" first, so the
            // eventual Confirmed/PaymentFailed transition has a valid state to move from.
            if (candidate.Status == BookingStatus.PaymentFailed)
            {
                candidate.TransitionTo(BookingStatus.PaymentPending, "Retrying payment.");
                await _bookingRepository.UpdateAsync(candidate);
            }

            if (!await HasEligibleProviderAsync(candidate.Id))
            {
                if (candidate.Id == lead.Id)
                {
                    return Error.Business(
                        "Payment.NoProviderAvailable",
                        "No service professional is currently available for this date and time. Please choose a different slot.");
                }

                await _releaseService.ExpireAsync(candidate, "No professional available for this date; removed from the prepaid purchase.");
                continue;
            }

            members.Add(candidate);
        }

        var existingTransactions = (await _paymentRepository.ListByBookingIdsAsync(members.Select(m => m.Id).ToList()))
            .ToDictionary(t => t.BookingId);

        foreach (var member in members)
        {
            if (existingTransactions.TryGetValue(member.Id, out var existing)
                && existing.Status is PaymentTransactionStatus.Cancelled or PaymentTransactionStatus.Success or PaymentTransactionStatus.Pending)
            {
                // A member can only be Pending/Success/Cancelled here through a stray ordinary-path
                // order; refusing keeps two live orders from ever covering one booking.
                return Error.Business("Payment.PrepaidMemberNotPayable", "One of the visits in this purchase already has a payment in progress.");
            }
        }

        decimal total = members.Sum(m => m.TotalPayableSnapshot);
        var gatewayResult = await _gateway.CreateOrderAsync(
            new GatewayCreateOrderRequest(
                lead.Id, total, Currency, lead.Id.ToString("N"),
                CustomerName: lead.CustomerNameSnapshot, CustomerMobile: lead.CustomerMobileSnapshot));

        var group = new PaymentGroup(Guid.NewGuid(), customerId, lead.Id, gatewayResult.GatewayOrderId, total, Currency, members.Count);
        var newTransactions = new List<PaymentTransaction>();
        var retriedTransactions = new List<PaymentTransaction>();
        int position = 0;
        foreach (var member in members)
        {
            // The gateway only ever sees the group's order id; each member's attempt gets a
            // synthetic id (attempt ids must stay unique) that points back at the group.
            string memberOrderId = $"{group.GatewayOrderId}~{++position}";
            if (existingTransactions.TryGetValue(member.Id, out var retried))
            {
                retried.StartAttempt(Guid.NewGuid(), memberOrderId, group.Id);
                retriedTransactions.Add(retried);
            }
            else
            {
                var transaction = new PaymentTransaction(
                    Guid.NewGuid(), member.Id, customerId, member.TotalPayableSnapshot, Currency, Guid.NewGuid().ToString("N"));
                transaction.StartAttempt(Guid.NewGuid(), memberOrderId, group.Id);
                newTransactions.Add(transaction);
            }
        }

        await _groupRepository.CreateAsync(group, newTransactions, retriedTransactions);

        return Result.Success(ToGroupOrderResponse(
            group, newTransactions.Concat(retriedTransactions).First(t => t.BookingId == lead.Id), gatewayResult,
            await SkippedDatesAsync(plan, planBookings, lead)));
    }

    private async Task<PaymentOrderResponse> ToGroupOrderResponseAsync(
        Booking lead, PaymentGroup group, RecurringBookingPlan plan, IReadOnlyList<Booking> planBookings)
    {
        var leadTransaction = await _paymentRepository.GetByBookingIdAsync(lead.Id)
            ?? throw new InvalidOperationException($"Payment group {group.Id} exists but its lead booking {lead.Id} has no payment transaction.");

        var gatewayResult = await _gateway.CreateOrderAsync(new GatewayCreateOrderRequest(
            lead.Id, group.TotalAmount, group.Currency, lead.Id.ToString("N"),
            CustomerName: lead.CustomerNameSnapshot, CustomerMobile: lead.CustomerMobileSnapshot,
            ExistingGatewayOrderId: group.GatewayOrderId));

        return ToGroupOrderResponse(group, leadTransaction, gatewayResult, await SkippedDatesAsync(plan, planBookings, lead));
    }

    private static PaymentOrderResponse ToGroupOrderResponse(
        PaymentGroup group, PaymentTransaction leadTransaction, GatewayOrderResult gatewayResult, IReadOnlyList<DateOnly> skippedDates)
    {
        var attempt = leadTransaction.LatestAttempt!;
        return new PaymentOrderResponse(
            leadTransaction.Id, attempt.Id, group.GatewayOrderId, group.TotalAmount, group.Currency, attempt.AttemptNumber, group.CreatedAtUtc,
            gatewayResult.CheckoutRedirectUrl, gatewayResult.CheckoutFormFields, group.VisitCount, skippedDates);
    }

    /// <summary>
    /// The dates of this purchase that were not booked: the ones the scheduler
    /// skipped (slot full, address gone...) plus the ones released just now
    /// because nobody could serve them. Derived from persisted state - the
    /// occurrence log and the released bookings - so the idempotent "order
    /// already in flight" path reports the same list as the first call did.
    /// </summary>
    private async Task<IReadOnlyList<DateOnly>> SkippedDatesAsync(RecurringBookingPlan plan, IReadOnlyList<Booking> planBookings, Booking lead)
    {
        var windowStart = lead.SlotDate;
        var windowEnd = plan.PrepaidThroughDate ?? DateOnly.MaxValue;

        var skipped = (await _occurrenceRepository.ListByPlanAsync(plan.Id))
            .Where(o => !o.Outcome.CreatedBooking() && o.ScheduledDate >= windowStart && o.ScheduledDate <= windowEnd)
            .Select(o => o.ScheduledDate);

        var released = planBookings
            .Where(b => b.Status == BookingStatus.Expired && b.SlotDate >= windowStart && b.SlotDate <= windowEnd)
            .Select(b => b.SlotDate);

        return skipped.Concat(released).Distinct().OrderBy(d => d).ToList();
    }

    /// <summary>
    /// Rebuilds a hosted-checkout gateway's redirect form for an
    /// already-existing attempt, reusing its persisted <see cref="PaymentAttempt.GatewayOrderId"/>
    /// rather than minting a new one - used by every branch of
    /// <see cref="CreateOrderAsync"/> that hands back an attempt it did not
    /// just start itself (the idempotent-Pending path, and the losing side
    /// of the concurrent-create race).
    /// </summary>
    private async Task<PaymentOrderResponse> ToOrderResponseAsync(Booking booking, PaymentTransaction transaction, PaymentAttempt attempt)
    {
        var gatewayResult = await _gateway.CreateOrderAsync(new GatewayCreateOrderRequest(
            booking.Id, transaction.Amount, transaction.Currency, booking.Id.ToString("N"),
            CustomerName: booking.CustomerNameSnapshot, CustomerMobile: booking.CustomerMobileSnapshot,
            ExistingGatewayOrderId: attempt.GatewayOrderId));

        return ToOrderResponse(transaction, attempt, gatewayResult);
    }

    private static PaymentOrderResponse ToOrderResponse(PaymentTransaction transaction, PaymentAttempt attempt, GatewayOrderResult? gatewayResult = null) => new(
        transaction.Id, attempt.Id, attempt.GatewayOrderId, transaction.Amount, transaction.Currency, attempt.AttemptNumber, attempt.CreatedAtUtc,
        gatewayResult?.CheckoutRedirectUrl, gatewayResult?.CheckoutFormFields);

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
