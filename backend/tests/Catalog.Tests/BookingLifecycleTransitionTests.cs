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
/// 13.1's 13-state matrix, not derived by reading BookingLifecycle.cs's
/// source at test-write time reflectively) so this suite actually catches a
/// future accidental edit to that table - a test that just replayed
/// BookingLifecycle's own dictionary back at itself would always pass.
///
/// One intentional addition beyond the original SRS 13.1 matrix: Assigned
/// -&gt; AwaitingFulfilment (task 159). SRS 31.1's transition list is
/// explicitly "Examples", not exhaustive, so this does not contradict it -
/// when the provider assigned to a booking rejects the job,
/// <c>IBookingProviderAssignmentService.RejectAsync</c> returns the booking to
/// AwaitingFulfilment so it re-enters the assignable pool for manual admin
/// reassignment (PROVIDER.md OPEN DECISIONS #1).
/// </summary>
public sealed class BookingLifecycleTransitionTests
{
    private static readonly IReadOnlyDictionary<BookingStatus, BookingStatus[]> ExpectedTransitions =
        new Dictionary<BookingStatus, BookingStatus[]>
        {
            [BookingStatus.Initiated] = [BookingStatus.PaymentPending, BookingStatus.CancelledByCustomer],
            [BookingStatus.PaymentPending] = [BookingStatus.Confirmed, BookingStatus.PaymentFailed, BookingStatus.CancelledByCustomer],
            [BookingStatus.PaymentFailed] = [BookingStatus.PaymentPending, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.Confirmed] = [BookingStatus.AwaitingFulfilment, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.AwaitingFulfilment] = [BookingStatus.Assigned, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.Assigned] = [BookingStatus.InProgress, BookingStatus.AwaitingFulfilment, BookingStatus.Rescheduled, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.InProgress] = [BookingStatus.Completed, BookingStatus.CancelledByAdmin],
            [BookingStatus.Completed] = [BookingStatus.RefundPending],
            [BookingStatus.CancelledByCustomer] = [BookingStatus.RefundPending, BookingStatus.Refunded],
            [BookingStatus.CancelledByAdmin] = [BookingStatus.RefundPending, BookingStatus.Refunded],
            [BookingStatus.Rescheduled] = [BookingStatus.AwaitingFulfilment, BookingStatus.CancelledByCustomer, BookingStatus.CancelledByAdmin],
            [BookingStatus.RefundPending] = [BookingStatus.Refunded],
            [BookingStatus.Refunded] = [],
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

    /// <summary>Every one of the 13×13 (from, to) combinations, checked against the independently-authored expected table.</summary>
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
    public void Refunded_is_the_only_terminal_state()
    {
        Enum.GetValues<BookingStatus>()
            .Where(BookingLifecycle.IsTerminal)
            .Should().BeEquivalentTo([BookingStatus.Refunded]);
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
}
