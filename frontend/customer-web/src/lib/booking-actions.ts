import { BookingProviderAssignmentStatus, BookingStatus } from "./types";

/**
 * What the booking detail page offers a customer at each stage of a booking.
 *
 * Kept free of React so the rules can be unit tested (`npm run test:unit`). The cancel / reschedule / review sets
 * mirror the backend, which stays the authority and refuses anything else: cancel and reschedule follow
 * BookingLifecycle.cs (a transition to CancelledByCustomer / Rescheduled must be legal from the booking's status) and
 * a review needs a Completed booking (ReviewService.EvaluateEligibilityAsync). Showing a button the backend is going
 * to refuse only sends the customer to a dead-end page.
 */

const CANCELLABLE_STATUSES: ReadonlySet<BookingStatus> = new Set([
  BookingStatus.Initiated,
  BookingStatus.PaymentPending,
  BookingStatus.PaymentFailed,
  BookingStatus.Confirmed,
  BookingStatus.AwaitingFulfilment,
  BookingStatus.Assigned,
  BookingStatus.ProviderEnRoute,
  BookingStatus.ProviderArrived,
  BookingStatus.Rescheduled,
]);

const RESCHEDULABLE_STATUSES: ReadonlySet<BookingStatus> = new Set([
  BookingStatus.Confirmed,
  BookingStatus.AwaitingFulfilment,
  BookingStatus.Assigned,
  BookingStatus.ProviderEnRoute,
  BookingStatus.ProviderArrived,
]);

/** Before any payment is taken, or a booking that lapsed without one. */
const PAYMENT_NOT_TAKEN_STATUSES: ReadonlySet<BookingStatus> = new Set([
  BookingStatus.Initiated,
  BookingStatus.PaymentPending,
  BookingStatus.PaymentFailed,
  BookingStatus.Expired,
]);

export function canCancelBooking(status: BookingStatus): boolean {
  return CANCELLABLE_STATUSES.has(status);
}

export function canRescheduleBooking(status: BookingStatus): boolean {
  return RESCHEDULABLE_STATUSES.has(status);
}

export function canReviewBooking(status: BookingStatus): boolean {
  return status === BookingStatus.Completed;
}

/** A repeat plan is offered off a booking that is real: not one still waiting on, or that never got, its payment. */
export function canSetUpRecurringFromBooking(status: BookingStatus): boolean {
  return !PAYMENT_NOT_TAKEN_STATUSES.has(status);
}

/**
 * The total's label on the price card. "Amount paid" is only true once payment was taken; a booking still awaiting
 * payment shows what is due, and one that was cancelled or lapsed shows a neutral total (it may or may not have been
 * paid, and the status does not say).
 */
export function bookingTotalLabel(status: BookingStatus): string {
  switch (status) {
    case BookingStatus.Initiated:
    case BookingStatus.PaymentPending:
    case BookingStatus.PaymentFailed:
      return "Amount due";
    case BookingStatus.Expired:
    case BookingStatus.CancelledByCustomer:
    case BookingStatus.CancelledByAdmin:
    case BookingStatus.RefundPending:
    case BookingStatus.Refunded:
      return "Total";
    default:
      return "Amount paid";
  }
}

export interface ProfessionalProgress {
  /** Headline wording, also the timeline node's title. */
  label: string;
  /** Short pill next to the professional's name. */
  badge: string;
  /** The line under the timeline node. */
  detail: string;
}

/**
 * Where the visit is once the professional has accepted and set off. The assignment row stays "Accepted" through the
 * whole visit (only the booking's own status moves on), so reading the assignment alone leaves "Professional
 * confirmed" on screen while they are already at the door or working. Null before then (or for any other assignment
 * state): the assignment wording is then the right one.
 */
export function professionalProgress(
  bookingStatus: BookingStatus,
  assignment: BookingProviderAssignmentStatus | null,
): ProfessionalProgress | null {
  if (assignment !== BookingProviderAssignmentStatus.Accepted) return null;

  switch (bookingStatus) {
    case BookingStatus.ProviderEnRoute:
      return {
        label: "Your professional is on the way",
        badge: "On the way",
        detail: "They have set off and will reach you in your slot window.",
      };
    case BookingStatus.ProviderArrived:
      return {
        label: "Your professional has arrived",
        badge: "Arrived",
        detail: "They are at your address and will start shortly.",
      };
    case BookingStatus.InProgress:
      return {
        label: "Service in progress",
        badge: "In progress",
        detail: "Your professional is working on your booking.",
      };
    default:
      return null;
  }
}
