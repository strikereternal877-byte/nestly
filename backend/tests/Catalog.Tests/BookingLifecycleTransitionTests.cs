using FluentAssertions;
using Nestly.Application;
using Nestly.Domain;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 66a: exhaustive coverage of the booking state transition
/// matrix (SRS 13.1-13.2), plus full lifecycle walks through
/// <see cref="Booking"/> itself.
///
/// <see cref="ExpectedTransitions"/> below is authored independently of
/// <see cref="BookingLifecycle"/>'s own table (hand-transcribed from SRS
/// 13.1's state matrix, not derived by reading BookingLifecycle.cs's
/// source at test-write time reflectively) so this suite actually catches a
/// future accidental edit to that table - a test that just replayed
/// BookingLifecycle's own dictionary back at itself would always pass.
///
/// Intentional additions beyond the original SRS 13.1 matrix (SRS 31.1's
/// transition list is explicitly "Examples", not exhaustive, so none of these
/// contradict it):
/// <list type="bullet">
/// <item>Assigned -&gt; AwaitingFulfilment (task 159) - when the provider
/// assigned to a booking rejects the job,
/// <c>IBookingProviderAssignmentService.RejectAsync</c> returns the booking to
/// AwaitingFulfilment so it re-enters the assignable pool for manual admin
/// reassignment (PROVIDER.md OPEN DECISIONS #1).</item>
/// <item>The Assigned -&gt; ProviderEnRoute -&gt; ProviderArrived -&gt;
/// InProgress tracking chain (task 264), which runs alongside the original
/// Assigned -&gt; InProgress edge rather than replacing it.</item>
/// <item>Initiated -&gt; Confirmed (task 331) - a booking with nothing payable
/// (an AMC entitlement redemption, a fully wallet-covered checkout, a discount
/// taking the total to zero) is confirmed by <c>BookingService.CreateAsync</c>
/// without ever awaiting a payment. It has to be its own edge rather than a
/// hop through PaymentPending: that state promises a payment that will never
/// be asked for, and is a dead end for such a booking, since
/// <c>PaymentTransaction</c> rejects a non-positive amount.</item>
/// </list>
/// </summary>
public sealed class BookingLifecycleTransitionTests
{
    private static readonly IReadOnlyDictionary<BookingStatus, BookingStatus[]> ExpectedTransitions =
        new Dictionary<BookingStatus, BookingStatus[]>
        {
            [BookingStatus.Initiated] = [BookingStatus.PaymentPending, BookingStatus.Confirmed, BookingStatus.CancelledByCustomer],
            // CancelledByAdmin is a deliberate addition to the SRS 13.1 matrix:
            // PaymentPending was the only cancellable state admin could not act
            // on, so ops handling a customer on the phone about a payment that
            // will never arrive could only wait out BookingExpirySweepJob's
            // 20-minute window - and nothing at all when the background job
            // server is disabled. Cancelling an unfunded booking raises no
            // refund (CancellationService resolves the refundable amount from
            // settled payments only) and returns the slot seat to the pool.
            [BookingStatus.PaymentPending] = [BookingStatus.Confirmed, BookingStatus.PaymentFailed, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin, BookingStatus.Expired],
            [BookingStatus.PaymentFailed] = [BookingStatus.PaymentPending, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.Confirmed] = [BookingStatus.AwaitingFulfilment, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.AwaitingFulfilment] = [BookingStatus.Assigned, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.Assigned] = [BookingStatus.ProviderEnRoute, BookingStatus.InProgress, BookingStatus.AwaitingFulfilment, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.ProviderEnRoute] = [BookingStatus.ProviderArrived, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.ProviderArrived] = [BookingStatus.InProgress, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.InProgress] = [BookingStatus.Completed, BookingStatus.CancelledByAdmin],
            [BookingStatus.Completed] = [BookingStatus.RefundPending],
            [BookingStatus.CancelledByCustomer] = [BookingStatus.RefundPending, BookingStatus.Refunded],
            [BookingStatus.CancelledByAdmin] = [BookingStatus.RefundPending, BookingStatus.Refunded],
            [BookingStatus.Rescheduled] = [BookingStatus.AwaitingFulfilment, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.RefundPending] = [BookingStatus.Refunded],
            [BookingStatus.Refunded] = [],
            [BookingStatus.Expired] = [],
        };

    public static IEnumerable<object[]> AllStatusPairs()
    {
        foreach (var from in Enum.GetValues<BookingStatus>())
        {
            foreach (var to in Enum.GetValues<BookingStatus>())
            {
                yield return [from, to];
            }
        }
    }

    /// <summary>Every one of the N×N (from, to) combinations, checked against the independently-authored expected table.</summary>
    [Theory]
    [MemberData(nameof(AllStatusPairs))]
    public void IsValidTransition_matches_the_SRS_13_1_matrix_for_every_pair(BookingStatus from, BookingStatus to)
    {
        bool expected = ExpectedTransitions[from].Contains(to);

        BookingLifecycle.IsValidTransition(from, to).Should().Be(
            expected,
            because: expected
                ? $"{from} -> {to} is listed as a legal transition"
                : $"{from} -> {to} is not listed as a legal transition");
    }

    [Fact]
    public void Every_status_appears_in_the_expected_table_so_no_status_is_silently_untested()
    {
        ExpectedTransitions.Keys.Should().BeEquivalentTo(Enum.GetValues<BookingStatus>());
    }

    public static IEnumerable<object[]> AllStatuses() =>
        Enum.GetValues<BookingStatus>().Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void IsTerminal_matches_having_zero_outgoing_transitions(BookingStatus status)
    {
        BookingLifecycle.IsTerminal(status).Should().Be(ExpectedTransitions[status].Length == 0);
    }

    [Fact]
    public void Refunded_and_Expired_are_the_only_terminal_states()
    {
        Enum.GetValues<BookingStatus>()
            .Where(BookingLifecycle.IsTerminal)
            .Should().BeEquivalentTo([BookingStatus.Refunded, BookingStatus.Expired]);
    }

    [Fact]
    public void PaymentPending_can_expire_and_Expired_is_terminal_with_no_refund_path()
    {
        var booking = NewBooking();
        booking.TransitionTo(BookingStatus.PaymentPending);

        booking.TransitionTo(BookingStatus.Expired, "Payment was not completed within the expiry window.");

        booking.Status.Should().Be(BookingStatus.Expired);
        booking.StatusHistory.Last().Reason.Should().Be("Payment was not completed within the expiry window.");
        BookingLifecycle.IsTerminal(booking.Status).Should().BeTrue();

        // Unlike CancelledByCustomer/CancelledByAdmin, an expired booking never
        // captured a payment - there is deliberately no RefundPending path.
        BookingLifecycle.IsValidTransition(BookingStatus.Expired, BookingStatus.RefundPending).Should().BeFalse();
    }

    /// <summary>
    /// Task 331: the zero-payable walk - Initiated straight to Confirmed and
    /// on through fulfilment, never touching PaymentPending. This is the path
    /// an AMC redemption booking takes end to end; before task 331 it stopped
    /// dead at PaymentPending, which no zero-amount payment could ever leave.
    /// </summary>
    [Fact]
    public void A_booking_with_nothing_payable_reaches_Completed_without_ever_entering_PaymentPending()
    {
        var booking = NewBooking();

        booking.TransitionTo(BookingStatus.Confirmed, "Nothing payable on this booking - confirmed without a payment.");
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        booking.TransitionTo(BookingStatus.Assigned);
        booking.TransitionTo(BookingStatus.InProgress);
        booking.TransitionTo(BookingStatus.Completed);

        booking.Status.Should().Be(BookingStatus.Completed);
        booking.StatusHistory.Select(h => h.ToStatus).Should().Equal(
            BookingStatus.Initiated, BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment,
            BookingStatus.Assigned, BookingStatus.InProgress, BookingStatus.Completed);
        booking.StatusHistory[1].Reason.Should().Be("Nothing payable on this booking - confirmed without a payment.");
    }

    // --- Full lifecycle walks through the real aggregate, not just the static table ---

    private static Booking NewBooking()
    {
        var customer = new Customer(Guid.NewGuid(), "9" + Guid.NewGuid().ToString("N")[..9], "Asha Rao", CustomerStatus.Active);
        var address = new AddressSnapshot("Home", "221B Baker Street", null, null, "560001", "Bengaluru", "Karnataka", 12.9716m, 77.5946m, "Asha Rao", "9876543210");
        var slot = new SlotSnapshot(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), "Morning", TimeSpan.FromHours(9), TimeSpan.FromHours(13));
        var price = new PriceSnapshot(500m, 1, 500m, 0m, 50m, 550m, 18m, 99m, 10m, 659m);
        return new Booking(Guid.NewGuid(), customer.Id, new CustomerSnapshot(customer.Name, customer.Mobile), null, address, slot, price);
    }

    [Fact]
    public void Happy_path_from_Initiated_through_Completed_to_Refunded_records_the_full_timeline_in_order()
    {
        var booking = NewBooking();

        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        booking.TransitionTo(BookingStatus.Assigned);
        booking.TransitionTo(BookingStatus.InProgress);
        booking.TransitionTo(BookingStatus.Completed);
        booking.TransitionTo(BookingStatus.RefundPending);
        booking.TransitionTo(BookingStatus.Refunded);

        booking.Status.Should().Be(BookingStatus.Refunded);
        booking.StatusHistory.Select(h => h.ToStatus).Should().Equal(
            BookingStatus.Initiated, BookingStatus.PaymentPending, BookingStatus.Confirmed,
            BookingStatus.AwaitingFulfilment, BookingStatus.Assigned, BookingStatus.InProgress,
            BookingStatus.Completed, BookingStatus.RefundPending, BookingStatus.Refunded);
        BookingLifecycle.IsTerminal(booking.Status).Should().BeTrue();
    }

    [Fact]
    public void Cancellation_path_from_Initiated_skips_straight_to_CancelledByCustomer_then_refunds()
    {
        var booking = NewBooking();

        booking.TransitionTo(BookingStatus.CancelledByCustomer, "Customer changed their mind.");
        booking.TransitionTo(BookingStatus.RefundPending);
        booking.TransitionTo(BookingStatus.Refunded);

        booking.StatusHistory.Select(h => h.ToStatus).Should().Equal(
            BookingStatus.Initiated, BookingStatus.CancelledByCustomer, BookingStatus.RefundPending, BookingStatus.Refunded);
        booking.StatusHistory[1].Reason.Should().Be("Customer changed their mind.");
    }

    [Fact]
    public void Reschedule_path_returns_to_AwaitingFulfilment_rather_than_restarting_from_Initiated()
    {
        var booking = NewBooking();

        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.Rescheduled, "Customer asked for a later slot.");
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);

        booking.Status.Should().Be(BookingStatus.AwaitingFulfilment);

        // Rescheduled never legally leads back into Initiated - re-affirms
        // AddItem stays locked even along this path (task 56d).
        BookingLifecycle.IsValidTransition(BookingStatus.Rescheduled, BookingStatus.Initiated).Should().BeFalse();
    }

    [Fact]
    public void Skipping_a_state_in_the_happy_path_is_rejected()
    {
        var booking = NewBooking();
        booking.TransitionTo(BookingStatus.PaymentPending);

        // Confirmed -> InProgress skips AwaitingFulfilment/Assigned entirely.
        var confirmThenSkip = () =>
        {
            booking.TransitionTo(BookingStatus.Confirmed);
            booking.TransitionTo(BookingStatus.InProgress);
        };

        confirmThenSkip.Should().Throw<InvalidOperationException>();
        booking.Status.Should().Be(BookingStatus.Confirmed, "the rejected transition must not have moved status past the last legal one");
    }

    [Fact]
    public void PaymentFailed_allows_retrying_payment_but_not_jumping_straight_to_Confirmed()
    {
        var booking = NewBooking();
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.PaymentFailed, "Card declined.");

        BookingLifecycle.IsValidTransition(BookingStatus.PaymentFailed, BookingStatus.PaymentPending).Should().BeTrue();
        BookingLifecycle.IsValidTransition(BookingStatus.PaymentFailed, BookingStatus.Confirmed).Should().BeFalse();

        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.Status.Should().Be(BookingStatus.PaymentPending);
    }

    // --- Task 264: the ProviderEnRoute/ProviderArrived tracking states ---

    private static Booking BookingAtAssigned()
    {
        var booking = NewBooking();
        booking.TransitionTo(BookingStatus.PaymentPending);
        booking.TransitionTo(BookingStatus.Confirmed);
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);
        booking.TransitionTo(BookingStatus.Assigned);
        return booking;
    }

    [Fact]
    public void Tracking_chain_walks_Assigned_to_EnRoute_to_Arrived_to_InProgress_recording_every_hop()
    {
        var booking = BookingAtAssigned();

        booking.TransitionTo(BookingStatus.ProviderEnRoute, "Provider set off.");
        booking.TransitionTo(BookingStatus.ProviderArrived, "Provider reached the address.");
        booking.TransitionTo(BookingStatus.InProgress, "Provider started the job.");
        booking.TransitionTo(BookingStatus.Completed);

        booking.Status.Should().Be(BookingStatus.Completed);
        booking.StatusHistory.Select(h => h.ToStatus).Should().Equal(
            BookingStatus.Initiated, BookingStatus.PaymentPending, BookingStatus.Confirmed,
            BookingStatus.AwaitingFulfilment, BookingStatus.Assigned, BookingStatus.ProviderEnRoute,
            BookingStatus.ProviderArrived, BookingStatus.InProgress, BookingStatus.Completed);
    }

    /// <summary>
    /// The regression this task's transition table most has to protect:
    /// tapping en-route is optional, so a provider who goes straight from
    /// Assigned to starting work must not be blocked by the new chain.
    /// </summary>
    [Fact]
    public void Assigned_can_still_go_straight_to_InProgress_without_passing_through_the_tracking_states()
    {
        BookingLifecycle.IsValidTransition(BookingStatus.Assigned, BookingStatus.InProgress).Should().BeTrue();

        var booking = BookingAtAssigned();
        booking.TransitionTo(BookingStatus.InProgress);

        booking.Status.Should().Be(BookingStatus.InProgress);
        booking.StatusHistory.Select(h => h.ToStatus).Should().NotContain(
            [BookingStatus.ProviderEnRoute, BookingStatus.ProviderArrived]);
    }

    [Theory]
    [InlineData(BookingStatus.ProviderArrived, BookingStatus.ProviderEnRoute)]
    [InlineData(BookingStatus.ProviderArrived, BookingStatus.Assigned)]
    [InlineData(BookingStatus.ProviderEnRoute, BookingStatus.Assigned)]
    [InlineData(BookingStatus.InProgress, BookingStatus.ProviderArrived)]
    [InlineData(BookingStatus.InProgress, BookingStatus.ProviderEnRoute)]
    public void Tracking_states_never_run_backwards(BookingStatus from, BookingStatus to)
    {
        BookingLifecycle.IsValidTransition(from, to).Should().BeFalse();
    }

    [Fact]
    public void EnRoute_cannot_skip_Arrived_and_Assigned_cannot_skip_straight_to_Arrived()
    {
        BookingLifecycle.IsValidTransition(BookingStatus.ProviderEnRoute, BookingStatus.InProgress).Should().BeFalse();
        BookingLifecycle.IsValidTransition(BookingStatus.Assigned, BookingStatus.ProviderArrived).Should().BeFalse();
    }

    [Theory]
    [InlineData(BookingStatus.ProviderEnRoute)]
    [InlineData(BookingStatus.ProviderArrived)]
    public void Cancel_and_reschedule_stay_available_from_both_tracking_states(BookingStatus trackingStatus)
    {
        BookingLifecycle.IsValidTransition(trackingStatus, BookingStatus.CancelledByCustomer).Should().BeTrue();
        BookingLifecycle.IsValidTransition(trackingStatus, BookingStatus.CancelledByAdmin).Should().BeTrue();
        BookingLifecycle.IsValidTransition(trackingStatus, BookingStatus.Rescheduled).Should().BeTrue();
    }

    [Fact]
    public void A_customer_can_still_cancel_while_the_provider_is_on_the_way()
    {
        var booking = BookingAtAssigned();
        booking.TransitionTo(BookingStatus.ProviderEnRoute);

        booking.TransitionTo(BookingStatus.CancelledByCustomer, "Customer no longer needs the service.");
        booking.TransitionTo(BookingStatus.RefundPending);

        booking.Status.Should().Be(BookingStatus.RefundPending);
    }

    [Fact]
    public void A_booking_can_still_be_rescheduled_after_the_provider_has_arrived()
    {
        var booking = BookingAtAssigned();
        booking.TransitionTo(BookingStatus.ProviderEnRoute);
        booking.TransitionTo(BookingStatus.ProviderArrived);

        booking.TransitionTo(BookingStatus.Rescheduled, "Customer was not home.");
        booking.TransitionTo(BookingStatus.AwaitingFulfilment);

        booking.Status.Should().Be(BookingStatus.AwaitingFulfilment);
    }

    [Theory]
    [InlineData(BookingStatus.ProviderEnRoute)]
    [InlineData(BookingStatus.ProviderArrived)]
    public void Neither_tracking_state_is_terminal(BookingStatus trackingStatus)
    {
        BookingLifecycle.IsTerminal(trackingStatus).Should().BeFalse();
    }
}
