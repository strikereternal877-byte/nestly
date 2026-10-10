import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  bookingTotalLabel,
  canCancelBooking,
  canRescheduleBooking,
  canReviewBooking,
  canSetUpRecurringFromBooking,
  professionalProgress,
} from "./booking-actions";
import { BookingProviderAssignmentStatus, BookingStatus } from "./types";

/**
 * What the booking detail page offers at each stage. The cancel / reschedule expectations are the transitions
 * BookingLifecycle.cs allows (to CancelledByCustomer / Rescheduled), written out status by status so a change on
 * either side shows up here.
 */

const ALL_STATUSES = Object.values(BookingStatus).filter((value): value is BookingStatus => typeof value === "number");

function statusesWhere(predicate: (status: BookingStatus) => boolean): string[] {
  return ALL_STATUSES.filter(predicate).map((status) => BookingStatus[status]);
}

describe("canCancelBooking", () => {
  it("is offered up to the visit, including while the professional is on the way or at the door", () => {
    assert.deepEqual(statusesWhere(canCancelBooking).sort(), [
      "Assigned",
      "AwaitingFulfilment",
      "Confirmed",
      "Initiated",
      "PaymentFailed",
      "PaymentPending",
      "ProviderArrived",
      "ProviderEnRoute",
      "Rescheduled",
    ]);
  });

  it("is not offered once the service has started (only an admin can cancel then) or the booking is closed", () => {
    for (const status of [
      BookingStatus.InProgress,
      BookingStatus.Completed,
      BookingStatus.CancelledByCustomer,
      BookingStatus.CancelledByAdmin,
      BookingStatus.RefundPending,
      BookingStatus.Refunded,
      BookingStatus.Expired,
    ]) {
      assert.equal(canCancelBooking(status), false, BookingStatus[status]);
    }
  });
});

describe("canRescheduleBooking", () => {
  it("is offered only once the booking is confirmed and until the service starts", () => {
    assert.deepEqual(statusesWhere(canRescheduleBooking).sort(), [
      "Assigned",
      "AwaitingFulfilment",
      "Confirmed",
      "ProviderArrived",
      "ProviderEnRoute",
    ]);
  });

  it("is not offered while payment is outstanding, in progress, or closed", () => {
    for (const status of [
      BookingStatus.Initiated,
      BookingStatus.PaymentPending,
      BookingStatus.PaymentFailed,
      BookingStatus.InProgress,
      BookingStatus.Completed,
      BookingStatus.Rescheduled,
      BookingStatus.Expired,
    ]) {
      assert.equal(canRescheduleBooking(status), false, BookingStatus[status]);
    }
  });
});

describe("canReviewBooking", () => {
  it("is offered for a completed booking and nothing else", () => {
    assert.deepEqual(statusesWhere(canReviewBooking), ["Completed"]);
  });
});

describe("canSetUpRecurringFromBooking", () => {
  it("is not offered off a booking that is unpaid or never got paid", () => {
    for (const status of [BookingStatus.Initiated, BookingStatus.PaymentPending, BookingStatus.PaymentFailed, BookingStatus.Expired]) {
      assert.equal(canSetUpRecurringFromBooking(status), false, BookingStatus[status]);
    }
  });

  it("is offered off a real booking", () => {
    for (const status of [BookingStatus.Confirmed, BookingStatus.InProgress, BookingStatus.Completed]) {
      assert.equal(canSetUpRecurringFromBooking(status), true, BookingStatus[status]);
    }
  });
});

describe("bookingTotalLabel", () => {
  it("says what is due while payment is outstanding, never 'paid'", () => {
    for (const status of [BookingStatus.Initiated, BookingStatus.PaymentPending, BookingStatus.PaymentFailed]) {
      assert.equal(bookingTotalLabel(status), "Amount due", BookingStatus[status]);
    }
  });

  it("says 'Amount paid' once payment was taken", () => {
    for (const status of [
      BookingStatus.Confirmed,
      BookingStatus.AwaitingFulfilment,
      BookingStatus.Assigned,
      BookingStatus.ProviderEnRoute,
      BookingStatus.ProviderArrived,
      BookingStatus.InProgress,
      BookingStatus.Completed,
    ]) {
      assert.equal(bookingTotalLabel(status), "Amount paid", BookingStatus[status]);
    }
  });

  it("is a neutral total for a lapsed, cancelled or refunded booking", () => {
    for (const status of [
      BookingStatus.Expired,
      BookingStatus.CancelledByCustomer,
      BookingStatus.CancelledByAdmin,
      BookingStatus.RefundPending,
      BookingStatus.Refunded,
    ]) {
      assert.equal(bookingTotalLabel(status), "Total", BookingStatus[status]);
    }
  });
});

describe("professionalProgress", () => {
  const ACCEPTED = BookingProviderAssignmentStatus.Accepted;

  it("follows the booking once the accepted professional has set off, instead of staying on 'confirmed'", () => {
    assert.equal(professionalProgress(BookingStatus.ProviderEnRoute, ACCEPTED)?.badge, "On the way");
    assert.equal(professionalProgress(BookingStatus.ProviderArrived, ACCEPTED)?.badge, "Arrived");

    const inProgress = professionalProgress(BookingStatus.InProgress, ACCEPTED);
    assert.equal(inProgress?.label, "Service in progress");
    assert.equal(inProgress?.badge, "In progress");
  });

  it("is null before the visit starts, so the assignment's own wording ('Professional confirmed') applies", () => {
    for (const status of [BookingStatus.Confirmed, BookingStatus.AwaitingFulfilment, BookingStatus.Assigned]) {
      assert.equal(professionalProgress(status, ACCEPTED), null, BookingStatus[status]);
    }
  });

  it("is null for any assignment that is not an accepted one", () => {
    for (const assignment of [
      null,
      BookingProviderAssignmentStatus.Assigned,
      BookingProviderAssignmentStatus.Rejected,
      BookingProviderAssignmentStatus.Completed,
    ]) {
      assert.equal(professionalProgress(BookingStatus.InProgress, assignment), null, String(assignment));
    }
  });
});
