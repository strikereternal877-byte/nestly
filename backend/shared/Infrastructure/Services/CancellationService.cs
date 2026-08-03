using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Bookings;
using Nestly.Application.Cancellations;
using Nestly.Application.Payments;
using Nestly.Application.Refunds;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Options;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// Cancellation eligibility, fee/refund computation, and confirmation (SRS
/// 11.14, 32.2, tasks 80a-c, 81). Eligibility is derived entirely from
/// <see cref="BookingLifecycle"/> - a booking can be cancelled by the
/// customer exactly when the lifecycle already allows the transition to
/// <see cref="BookingStatus.CancelledByCustomer"/>, which naturally encodes
/// "not already cancelled/completed" and "service hasn't started yet"
/// (InProgress only allows CancelledByAdmin) without duplicating that rule
/// here.
///
/// Fee/refund computation reuses <see cref="CancellationFeeCalculator"/> for
/// the pure math and <see cref="IRefundService"/> (Phase 4) for actually
/// raising the refund - this service never talks to the payment gateway or
/// wallet directly.
/// </summary>
public class CancellationService : ICancellationService
{
    private readonly IBookingRepository _bookingRepository;
    private readonly IPaymentTransactionRepository _paymentRepository;
    private readonly IRefundTransactionRepository _refundTransactionRepository;
    private readonly IRefundService _refundService;
    private readonly ICancellationRepository _cancellationRepository;
    private readonly IBookingProviderAssignmentRepository _assignmentRepository;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationPolicyOptions _policy;

    public CancellationService(
        IBookingRepository bookingRepository,
        IPaymentTransactionRepository paymentRepository,
        IRefundTransactionRepository refundTransactionRepository,
        IRefundService refundService,
        ICancellationRepository cancellationRepository,
        IBookingProviderAssignmentRepository assignmentRepository,
        TimeProvider timeProvider,
        IOptions<CancellationPolicyOptions> policy)
    {
        _bookingRepository = bookingRepository;
        _paymentRepository = paymentRepository;
        _refundTransactionRepository = refundTransactionRepository;
        _refundService = refundService;
        _cancellationRepository = cancellationRepository;
        _assignmentRepository = assignmentRepository;
        _timeProvider = timeProvider;
        _policy = policy.Value;
    }

    public async Task<Result<CancellationPolicyResponse>> GetPolicyAsync(Guid customerId, Guid bookingId)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Cancellation.BookingNotFound", "The specified booking does not exist.");
        }

        bool eligible = BookingLifecycle.IsValidTransition(booking.Status, BookingStatus.CancelledByCustomer);
        if (!eligible)
        {
            return Result.Success(new CancellationPolicyResponse(
                IsEligible: false,
                IneligibilityReason: $"A booking in status '{booking.Status}' can no longer be cancelled by the customer.",
                WithinFreeCancellationWindow: false,
                CancellationFeeAmount: 0m,
                RefundAmount: 0m,
                RefundMethod: RefundMethod.Gateway,
                _policy.FreeCancellationWindowHours,
                _policy.LateCancellationFeePercentage));
        }

        var outcome = await ComputeOutcomeAsync(booking);

        return Result.Success(new CancellationPolicyResponse(
            IsEligible: true,
            IneligibilityReason: null,
            outcome.WithinFreeWindow,
            outcome.FeeAmount,
            outcome.RefundAmount,
            RefundMethod.Gateway,
            _policy.FreeCancellationWindowHours,
            _policy.LateCancellationFeePercentage));
    }

    public async Task<Result<CancellationOutcomeResponse>> CancelAsync(Guid customerId, Guid bookingId, CancelBookingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("Cancellation.ReasonRequired", "A cancellation reason is required.");
        }

        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null || booking.CustomerId != customerId)
        {
            return Error.NotFound("Cancellation.BookingNotFound", "The specified booking does not exist.");
        }

        if (!BookingLifecycle.IsValidTransition(booking.Status, BookingStatus.CancelledByCustomer))
        {
            return Error.Business(
                "Cancellation.NotEligible",
                $"A booking in status '{booking.Status}' can no longer be cancelled by the customer.");
        }

        return await ExecuteCancellationAsync(booking, BookingStatus.CancelledByCustomer, CancellationActor.Customer, request.Reason, internalNotes: null);
    }

    public async Task<Result<CancellationOutcomeResponse>> AdminCancelAsync(Guid bookingId, string reason, string? internalNotes = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation("Cancellation.ReasonRequired", "A cancellation reason is required.");
        }

        var booking = await _bookingRepository.GetByIdAsync(bookingId);
        if (booking is null)
        {
            return Error.NotFound("Cancellation.BookingNotFound", "The specified booking does not exist.");
        }

        if (!BookingLifecycle.IsValidTransition(booking.Status, BookingStatus.CancelledByAdmin))
        {
            return Error.Business(
                "Cancellation.NotEligible",
                $"A booking in status '{booking.Status}' can no longer be cancelled.");
        }

        return await ExecuteCancellationAsync(booking, BookingStatus.CancelledByAdmin, CancellationActor.Admin, reason, internalNotes);
    }

    /// <summary>
    /// Shared confirmation path for both <see cref="CancelAsync"/> and
    /// <see cref="AdminCancelAsync"/>: computes the fee/refund outcome via
    /// <see cref="CancellationFeeCalculator"/> (same policy math for either
    /// actor - task 117a composes this rather than reimplementing it),
    /// transitions the booking, raises a refund if one is owed, and records
    /// the <see cref="BookingCancellation"/> history row.
    /// </summary>
    private async Task<Result<CancellationOutcomeResponse>> ExecuteCancellationAsync(
        Booking booking, BookingStatus targetStatus, CancellationActor actor, string reason, string? internalNotes)
    {
        var outcome = await ComputeOutcomeAsync(booking);

        booking.TransitionTo(targetStatus, reason);
        await _bookingRepository.UpdateAsync(booking);

        // Task 208: a provider still Assigned/Accepted on this booking has no
        // way of hearing about the cancellation otherwise - ProviderJobService
        // derives their job status from this assignment row, not from the
        // booking itself.
        var activeAssignment = await _assignmentRepository.GetActiveByBookingAsync(booking.Id);
        if (activeAssignment is not null)
        {
            activeAssignment.Withdraw();
            await _assignmentRepository.UpdateAsync(activeAssignment);
        }

        Guid? refundTransactionId = null;
        RefundStatus? refundStatus = null;
        RefundMethod? refundMethod = null;

        if (outcome.RefundAmount > 0)
        {
            var refundResult = outcome.FeeAmount > 0
                ? await _refundService.InitiatePartialRefundAsync(booking.Id, outcome.RefundAmount, reason)
                : await _refundService.InitiateFullRefundAsync(booking.Id, reason);

            if (refundResult.IsFailure)
            {
                return refundResult.Error;
            }

            refundTransactionId = refundResult.Value.Id;
            refundStatus = refundResult.Value.Status;
            refundMethod = refundResult.Value.Method;
        }

        var cancellation = new BookingCancellation(
            Guid.NewGuid(),
            booking.Id,
            actor,
            reason,
            outcome.WithinFreeWindow,
            outcome.FeeAmount,
            outcome.RefundAmount,
            refundMethod,
            refundTransactionId,
            internalNotes);

        await _cancellationRepository.AddAsync(cancellation);

        // Re-fetch to report the booking's true post-refund status: a full
        // refund with nothing owed moves the booking straight past
        // CancelledByCustomer/CancelledByAdmin to RefundPending/Refunded
        // inside IRefundService.
        var finalBooking = await _bookingRepository.GetByIdAsync(booking.Id) ?? booking;

        return Result.Success(new CancellationOutcomeResponse(
            booking.Id,
            finalBooking.Status,
            outcome.WithinFreeWindow,
            outcome.FeeAmount,
            outcome.RefundAmount,
            refundStatus,
            refundMethod,
            refundTransactionId,
            cancellation.CreatedAtUtc));
    }

    private async Task<CancellationFeeCalculator.Outcome> ComputeOutcomeAsync(Booking booking)
    {
        decimal payableAmount = await ResolvePayableAmountAsync(booking.Id);

        DateTime slotStartUtc = booking.SlotDate.ToDateTime(TimeOnly.MinValue).Add(booking.SlotStartTimeSnapshot);
        DateTime now = _timeProvider.GetUtcNow().DateTime;
        TimeSpan timeUntilSlot = slotStartUtc - now;

        return CancellationFeeCalculator.Compute(
            payableAmount, timeUntilSlot, _policy.FreeCancellationWindowHours, _policy.LateCancellationFeePercentage);
    }

    /// <summary>What remains refundable on the booking's payment - 0 if it was never paid for or already fully refunded.</summary>
    private async Task<decimal> ResolvePayableAmountAsync(Guid bookingId)
    {
        var payment = await _paymentRepository.GetByBookingIdAsync(bookingId);
        if (payment is null || payment.Status != PaymentTransactionStatus.Success)
        {
            return 0m;
        }

        var priorRefunds = await _refundTransactionRepository.ListByPaymentTransactionAsync(payment.Id);
        decimal alreadyRefunded = priorRefunds.Where(r => r.Status != RefundStatus.Failed).Sum(r => r.Amount);
        decimal remaining = payment.Amount - alreadyRefunded;
        return remaining > 0 ? remaining : 0m;
    }
}
