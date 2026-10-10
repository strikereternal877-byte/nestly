"use client";

import Link from "next/link";
import { Fragment } from "react";
import type { ReactNode } from "react";
import { useVisibleAccountLinks } from "@/components/SiteHeader";
import { Badge, Card, Skeleton, cx } from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { SPRING } from "@/components/motion";
import { professionalProgress } from "@/lib/booking-actions";
import { motion } from "motion/react";
import {
  BookingProviderAssignmentStatus,
  BookingStatus,
  RecurringBookingPlanStatus,
  RecurringBookingRecurrenceFrequency,
  SupportTicketStatus,
} from "@/lib/types";
import type {
  BookingStatusTimelineEntry,
  PriceBreakdown,
  ServiceAddOnGroupSummary,
  ServiceVariantSummary,
} from "@/lib/types";

/**
 * Screen-level patterns shared by the customer-web booking, post-booking and
 * account screens (Phase 12 rows 218-220).
 *
 * Deliberately a separate file from `components/ui.tsx`: that kit is
 * byte-identical across customer-web, admin-web and provider-web, so anything
 * added there has to be ported to all three. Everything here is
 * customer-only — a booking progress rail and an INR price breakdown have no
 * meaning in the admin or provider apps — so it lives outside the frozen kit.
 *
 * No component here contains a hex value or a raw `neutral-*`/`black/10`
 * class; every visual value resolves through the tokens in app/globals.css.
 */

/* -------------------------------------------------------------------------- */
/* Formatting                                                                 */
/* -------------------------------------------------------------------------- */

/**
 * Money, always to two decimals with the rupee sign attached.
 *
 * Kept as `toFixed(2)` rather than `Intl.NumberFormat` on purpose: the
 * amounts rendered here are echoed straight back from server-calculated
 * decimals, and a locale-dependent grouping separator would make the same
 * booking read differently depending on the browser's locale.
 */
export function inr(amount: number): string {
  return `₹${amount.toFixed(2)}`;
}

/**
 * A `YYYY-MM-DD` calendar date as "Mon, 4 Aug 2026".
 *
 * Parsed as `${iso}T00:00:00` — local midnight — rather than `new Date(iso)`,
 * which JS parses as *UTC* midnight and therefore renders as the previous day
 * for every timezone behind UTC. Same class of defect as the
 * `toISOString().slice(0, 10)` bug lib/date.ts exists to prevent, in the
 * opposite direction.
 */
export function formatCalendarDate(iso: string): string {
  if (!iso) return "";
  const date = new Date(`${iso}T00:00:00`);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleDateString(undefined, {
    weekday: "short",
    day: "numeric",
    month: "short",
    year: "numeric",
  });
}

/** A ".NET TimeSpan" `hh:mm:ss` pair as "09:00–11:00". */
export function formatTimeRange(startTime: string, endTime: string): string {
  return `${startTime.slice(0, 5)}–${endTime.slice(0, 5)}`;
}

/**
 * A business-local wall-clock date-time with no zone suffix ("2026-10-03T05:00:00" - what the API sends for a
 * moment that is read against a slot's own time) as "Sat, 3 Oct, 05:00 AM". Parsed as local time on purpose: it is
 * already the business's wall clock, and treating it as UTC would shift it by the viewer's offset.
 */
export function formatLocalDateTime(local: string): string {
  const date = new Date(local);
  if (Number.isNaN(date.getTime())) return local;
  return date.toLocaleString(undefined, {
    weekday: "short",
    day: "numeric",
    month: "short",
    hour: "2-digit",
    minute: "2-digit",
  });
}

/** An ISO instant as a local date + time, for timeline and ledger rows. */
export function formatInstant(utc: string): string {
  const date = new Date(utc);
  if (Number.isNaN(date.getTime())) return utc;
  return date.toLocaleString(undefined, {
    day: "numeric",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

/**
 * An ISO instant as a local date alone, for values where the time of day is
 * noise: a billing date, the day a referral signed up.
 *
 * Separate from `formatCalendarDate`, which takes a `YYYY-MM-DD` and must not
 * be handed an instant — appending `T00:00:00` to one produces an unparseable
 * string and the raw ISO text leaks into the UI.
 */
export function formatInstantDate(utc: string): string {
  const date = new Date(utc);
  if (Number.isNaN(date.getTime())) return utc;
  return date.toLocaleDateString(undefined, {
    day: "numeric",
    month: "short",
    year: "numeric",
  });
}

/* -------------------------------------------------------------------------- */
/* Status vocabulary                                                          */
/* -------------------------------------------------------------------------- */

/**
 * Maps a booking state onto the shared status token scale, so the same colour
 * means the same thing on the list, the detail page and the confirmation.
 * Money-back states read `info` rather than `danger` — a refund in flight is
 * not an error, it is the system working.
 */
export function bookingStatusTone(status: BookingStatus): BadgeTone {
  switch (status) {
    case BookingStatus.Completed:
    case BookingStatus.Confirmed:
      return "success";
    case BookingStatus.Assigned:
    // The tracking states (task 264) read as the same live-service moment as
    // Assigned/InProgress, so they share its token rather than introducing a
    // fourth colour into an already-busy timeline.
    case BookingStatus.ProviderEnRoute:
    case BookingStatus.ProviderArrived:
    case BookingStatus.InProgress:
      return "brand";
    case BookingStatus.PaymentPending:
    case BookingStatus.AwaitingFulfilment:
    case BookingStatus.Initiated:
    case BookingStatus.Rescheduled:
      return "warning";
    case BookingStatus.PaymentFailed:
    case BookingStatus.CancelledByCustomer:
    case BookingStatus.CancelledByAdmin:
    case BookingStatus.Expired:
      return "danger";
    case BookingStatus.RefundPending:
    case BookingStatus.Refunded:
      return "info";
    default:
      return "neutral";
  }
}

/** Status pill for a booking, using the server's own label text. */
export function BookingStatusBadge({
  status,
  label,
}: {
  status: BookingStatus;
  label: string;
}) {
  return <Badge tone={bookingStatusTone(status)}>{label}</Badge>;
}

/**
 * Customer-facing wording for a `BookingProviderAssignment` row (task 208).
 * The booking's own status stays "Assigned" across both the offer and the
 * provider's accept, so this is the only signal that tells a customer their
 * professional has actually confirmed.
 */
export function providerAssignmentLabel(status: BookingProviderAssignmentStatus): string {
  switch (status) {
    case BookingProviderAssignmentStatus.Assigned:
      return "Professional assigned — awaiting their confirmation";
    case BookingProviderAssignmentStatus.Accepted:
      return "Professional confirmed";
    case BookingProviderAssignmentStatus.Rejected:
      return "Professional declined — finding you another";
    case BookingProviderAssignmentStatus.Reassigned:
      return "Reassigned to another professional";
    case BookingProviderAssignmentStatus.Withdrawn:
      return "Assignment withdrawn — finding you another";
    case BookingProviderAssignmentStatus.Completed:
      return "Job completed";
    default:
      return "Professional assignment updating";
  }
}

export function providerAssignmentTone(status: BookingProviderAssignmentStatus): BadgeTone {
  switch (status) {
    case BookingProviderAssignmentStatus.Accepted:
    case BookingProviderAssignmentStatus.Completed:
      return "success";
    case BookingProviderAssignmentStatus.Assigned:
      return "brand";
    case BookingProviderAssignmentStatus.Rejected:
    case BookingProviderAssignmentStatus.Withdrawn:
      return "warning";
    case BookingProviderAssignmentStatus.Reassigned:
      return "info";
    default:
      return "neutral";
  }
}

export function recurringPlanStatusTone(status: RecurringBookingPlanStatus): BadgeTone {
  switch (status) {
    case RecurringBookingPlanStatus.Active:
      return "success";
    case RecurringBookingPlanStatus.Paused:
      return "warning";
    case RecurringBookingPlanStatus.Cancelled:
      return "danger";
    case RecurringBookingPlanStatus.Completed:
      return "neutral";
    default:
      return "neutral";
  }
}

/**
 * How a recurrence frequency reads to a customer.
 *
 * One definition rather than three: the recurring-plan set-up form, the manage
 * list and the booking summary's "repeat this booking" opt-in (task 298) all
 * name the same three frequencies, and three private copies of these strings
 * had already started drifting ("Every 2 weeks" vs "Fortnightly").
 */
export function recurringFrequencyLabel(frequency: RecurringBookingRecurrenceFrequency): string {
  switch (frequency) {
    case RecurringBookingRecurrenceFrequency.Daily:
      return "Every day";
    case RecurringBookingRecurrenceFrequency.Weekly:
      return "Every week";
    case RecurringBookingRecurrenceFrequency.Biweekly:
      return "Every 2 weeks";
    case RecurringBookingRecurrenceFrequency.Monthly:
      return "Every month";
    default:
      return "Unknown";
  }
}

/** Picker options, shortest interval first (not the enum's ordinal order - Daily was appended last for wire compatibility). */
export const RECURRING_FREQUENCY_OPTIONS: {
  value: RecurringBookingRecurrenceFrequency;
  label: string;
}[] = [
  RecurringBookingRecurrenceFrequency.Daily,
  RecurringBookingRecurrenceFrequency.Weekly,
  RecurringBookingRecurrenceFrequency.Biweekly,
  RecurringBookingRecurrenceFrequency.Monthly,
].map((value) => ({ value, label: recurringFrequencyLabel(value) }));

/**
 * Segmented pill radiogroup over {@link RECURRING_FREQUENCY_OPTIONS} - the
 * exact same control was independently built on the booking summary page and
 * the standalone "new recurring booking" page; this is the one shared copy.
 */
export function FrequencyPicker({
  value,
  onChange,
  label,
}: {
  value: RecurringBookingRecurrenceFrequency;
  onChange: (value: RecurringBookingRecurrenceFrequency) => void;
  label: string;
}) {
  return (
    <div role="radiogroup" aria-label={label} className="flex flex-wrap gap-2">
      {RECURRING_FREQUENCY_OPTIONS.map((option) => {
        const isSelected = value === option.value;
        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={isSelected}
            onClick={() => onChange(option.value)}
            className={cx(
              "rounded-xl border px-3.5 py-2 text-sm font-medium transition duration-fast ease-out",
              isSelected
                ? "border-brand-600 bg-brand-600 text-fg-on-brand shadow-brand"
                : "border-line bg-surface text-fg hover:border-line-strong hover:bg-surface-2",
            )}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

export function supportStatusTone(status: SupportTicketStatus): BadgeTone {
  switch (status) {
    case SupportTicketStatus.Open:
      return "info";
    case SupportTicketStatus.InProgress:
      return "brand";
    case SupportTicketStatus.WaitingForCustomer:
      return "warning";
    case SupportTicketStatus.Escalated:
      return "danger";
    case SupportTicketStatus.Resolved:
      return "success";
    case SupportTicketStatus.Closed:
      return "neutral";
    default:
      return "neutral";
  }
}

/* -------------------------------------------------------------------------- */
/* Booking flow progress                                                      */
/* -------------------------------------------------------------------------- */

export const BOOKING_STEPS = ["Review", "Payment", "Confirmed"] as const;

/**
 * The three-step rail across booking summary → payment → success.
 *
 * An ordered list rather than a row of divs so the sequence is conveyed
 * without relying on the visual order, with `aria-current="step"` marking
 * where the customer is and a visually-hidden "Done"/"Current" suffix so the
 * state is not carried by colour alone.
 */
export function BookingProgress({ current }: { current: 0 | 1 | 2 }) {
  return (
    <nav aria-label="Booking progress" className="mb-6">
      <ol className="flex items-center gap-2 sm:gap-3">
        {BOOKING_STEPS.map((step, index) => {
          const isDone = index < current;
          const isCurrent = index === current;
          return (
            <li key={step} className="flex min-w-0 flex-1 items-center gap-2 sm:gap-3">
              <span
                aria-current={isCurrent ? "step" : undefined}
                className="flex min-w-0 items-center gap-2"
              >
                <span
                  aria-hidden
                  className={cx(
                    "flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-xs font-semibold transition duration-fast ease-out",
                    isDone && "bg-brand-600 text-fg-on-brand shadow-brand",
                    isCurrent && "bg-brand-600 text-fg-on-brand shadow-brand ring-4 ring-brand-600/20",
                    !isDone && !isCurrent && "bg-surface-3 text-fg-subtle",
                  )}
                >
                  {isDone ? (
                    <svg
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                      strokeWidth="3"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      className="h-3.5 w-3.5"
                    >
                      <path d="m5 13 4 4L19 7" />
                    </svg>
                  ) : (
                    index + 1
                  )}
                </span>
                <span
                  className={cx(
                    "truncate text-xs font-medium sm:text-sm",
                    isCurrent ? "text-fg" : "text-fg-muted",
                  )}
                >
                  {step}
                  <span className="sr-only">
                    {isDone ? " — done" : isCurrent ? " — current step" : " — not started"}
                  </span>
                </span>
              </span>
              {index < BOOKING_STEPS.length - 1 ? (
                <span
                  aria-hidden
                  className={cx(
                    "h-px flex-1 rounded-full",
                    isDone ? "bg-brand-600" : "bg-line",
                  )}
                />
              ) : null}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

/* -------------------------------------------------------------------------- */
/* Money                                                                      */
/* -------------------------------------------------------------------------- */

/**
 * One line of a price breakdown. `tone` is the only thing that varies:
 * `muted` for contributing lines, `discount` for a saving, `total` for the
 * figure the customer is actually agreeing to pay.
 */
export function PriceLine({
  label,
  value,
  tone = "muted",
  hint,
}: {
  label: ReactNode;
  value: string;
  tone?: "muted" | "strong" | "discount" | "total";
  hint?: string;
}) {
  return (
    <div
      className={cx(
        "flex items-baseline justify-between gap-4",
        tone === "muted" && "text-sm text-fg-muted",
        tone === "strong" && "text-sm font-medium text-fg",
        tone === "discount" && "text-sm font-medium text-success",
        tone === "total" && "text-base font-semibold text-fg",
      )}
    >
      <dt className="min-w-0">
        <span className="break-words">{label}</span>
        {hint ? <span className="mt-0.5 block text-xs text-fg-subtle">{hint}</span> : null}
      </dt>
      <dd className={cx("nums shrink-0", tone === "total" && "text-lg")}>{value}</dd>
    </div>
  );
}

/**
 * The canonical price breakdown, shared by the booking summary, the payment
 * screen and the booking detail so the same booking never adds up differently
 * on two screens.
 *
 * Grouped rather than flat: what the service costs, then what is added on top
 * (tax, fees), then the single number that matters. `discount` is optional —
 * pass it and `total` becomes the post-discount payable.
 */
export function PriceBreakdownList({
  breakdown,
  discount,
  walletCreditApplied,
  total,
  totalLabel = "Total payable",
}: {
  breakdown: PriceBreakdown;
  discount?: { code: string | null; amount: number } | null;
  /** Wallet balance applied at checkout (SRS 11.7.2, task 310) - its own line, separate from `discount`, since it stacks with a coupon rather than replacing it. */
  walletCreditApplied?: number | null;
  total: number;
  totalLabel?: string;
}) {
  return (
    <dl className="flex flex-col gap-2.5">
      <PriceLine
        label={breakdown.quantity > 1 ? `Base price × ${breakdown.quantity}` : "Base price"}
        value={inr(breakdown.baseTotal)}
        tone="strong"
      />

      {breakdown.addOnLineItems.map((item) => (
        <PriceLine
          key={item.addOnId}
          label={`${item.name} × ${item.quantity}`}
          value={inr(item.lineTotal)}
        />
      ))}

      {breakdown.visitCharge > 0 ? (
        <PriceLine label="Visit charge" value={inr(breakdown.visitCharge)} />
      ) : null}

      <div className="my-1 border-t border-line" />

      <PriceLine label="Subtotal" value={inr(breakdown.subtotal)} tone="strong" />
      <PriceLine label={`Tax (${breakdown.taxPercentage}%)`} value={inr(breakdown.taxAmount)} />
      {breakdown.platformFee > 0 ? (
        <PriceLine label="Platform fee" value={inr(breakdown.platformFee)} />
      ) : null}

      {discount && discount.amount > 0 ? (
        <PriceLine
          label={discount.code ? `Coupon (${discount.code})` : "Discount"}
          value={`−${inr(discount.amount)}`}
          tone="discount"
        />
      ) : null}

      {walletCreditApplied && walletCreditApplied > 0 ? (
        <PriceLine
          label="Wallet credit used"
          value={`−${inr(walletCreditApplied)}`}
          tone="discount"
        />
      ) : null}

      <div className="my-1 border-t border-line-strong" />

      <PriceLine label={totalLabel} value={inr(total)} tone="total" />
    </dl>
  );
}

/** Matching placeholder for `PriceBreakdownList` while the quote is in flight. */
export function PriceBreakdownSkeleton() {
  return (
    <div className="flex flex-col gap-3" aria-hidden>
      {[0, 1, 2].map((row) => (
        <div key={row} className="flex items-center justify-between gap-4">
          <Skeleton className="h-3.5 w-32" />
          <Skeleton className="h-3.5 w-16" />
        </div>
      ))}
      <div className="my-1 border-t border-line" />
      <div className="flex items-center justify-between gap-4">
        <Skeleton className="h-5 w-24" />
        <Skeleton className="h-5 w-24" />
      </div>
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Catalog selection (Phase 3 catalog redesign)                              */
/* -------------------------------------------------------------------------- */

/** Shared selected/idle treatment for a radio- or checkbox-backed option row, matching the address/add-on pickers already in the booking flow. */
export const OPTION_ROW = (checked: boolean) =>
  cx(
    "flex cursor-pointer items-center justify-between gap-3 rounded-lg border px-3 py-2 text-sm transition duration-fast ease-out",
    checked
      ? "border-brand-600/40 bg-brand-50 dark:bg-brand-500/10"
      : "border-line hover:border-line-strong hover:bg-surface-2",
  );

/**
 * Pick-one selector for a service's priced/timed variants (Phase 3 catalog
 * redesign). Renders nothing when `variants` is empty — a service with no
 * variants keeps booking at its flat price, unchanged.
 */
/**
 * Pick-one selector for a service's priced/timed variants (Phase 3 catalog
 * redesign, visual pass). Segmented pill buttons rather than a vertical
 * radio list - real `<button>`s with `aria-pressed`, since this is a
 * single-select toggle group rather than a form control needing native
 * radio semantics. Wraps onto multiple rows for services with more than a
 * handful of options (`flex-wrap`), so it never breaks on many variants.
 */
export function VariantPicker({
  variants,
  selectedId,
  onSelect,
}: {
  variants: ServiceVariantSummary[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  if (variants.length === 0) return null;

  return (
    <div role="group" aria-label="Choose an option">
      <p className="mb-2 text-sm font-medium text-fg">Choose an option</p>
      <div className="flex flex-wrap gap-2">
        {variants.map((variant) => {
          const selected = selectedId === variant.id;
          return (
            <motion.button
              key={variant.id}
              type="button"
              aria-pressed={selected}
              onClick={() => onSelect(variant.id)}
              whileTap={{ scale: 0.97 }}
              transition={SPRING}
              className={cx(
                "flex flex-col items-start gap-0.5 rounded-xl border px-3.5 py-2 text-left text-sm transition duration-fast ease-out",
                selected
                  ? "border-brand-600 bg-brand-600 text-fg-on-brand shadow-brand"
                  : "border-line bg-surface text-fg hover:border-line-strong hover:bg-surface-2",
              )}
            >
              <span className="font-medium">{variant.name}</span>
              <span className={cx("nums text-xs", selected ? "text-fg-on-brand/85" : "text-fg-muted")}>
                {variant.durationMinutes} min · {inr(variant.price)}
              </span>
            </motion.button>
          );
        })}
      </div>
    </div>
  );
}

/**
 * Selector for one add-on group (Phase 3 catalog redesign): radio behaviour
 * for a pick-one group, checkbox behaviour (bounded by `maxSelect`, when set)
 * for a pick-many group. `selectedIds` and `onToggle` operate on the full
 * add-on id set shared across every group and the ungrouped list, same as
 * the pre-Phase-3 flat checkbox list did.
 */
export function AddOnGroupSelector({
  group,
  selectedIds,
  onToggle,
}: {
  group: ServiceAddOnGroupSummary;
  selectedIds: Set<string>;
  onToggle: (addOnId: string) => void;
}) {
  const isSingle = group.selectionType === "Single";
  const selectedInGroup = group.addOns.filter((a) => selectedIds.has(a.id));
  const atCap = group.maxSelect !== null && selectedInGroup.length >= group.maxSelect;

  // A radio's onChange only fires for the newly-checked item, but onToggle
  // is a plain per-id flip - so a pick-one group has to explicitly untoggle
  // whichever sibling was previously selected in the same gesture.
  const selectInSingleGroup = (addOnId: string) => {
    for (const other of selectedInGroup) {
      if (other.id !== addOnId) onToggle(other.id);
    }
    if (!selectedIds.has(addOnId)) onToggle(addOnId);
  };

  return (
    <fieldset className="flex flex-col gap-2">
      <legend className="mb-1 text-sm font-medium text-fg">{group.name}</legend>
      {group.addOns.map((addOn) => {
        const checked = selectedIds.has(addOn.id);
        const disabled = !isSingle && !checked && atCap;
        return (
          <motion.label
            key={addOn.id}
            className={cx(OPTION_ROW(checked), disabled && "cursor-not-allowed opacity-50")}
            whileTap={disabled ? undefined : { scale: 0.98 }}
            transition={SPRING}
          >
            <span className="flex min-w-0 items-center gap-2.5">
              <input
                type={isSingle ? "radio" : "checkbox"}
                name={isSingle ? `addon-group-${group.id}` : undefined}
                checked={checked}
                disabled={disabled}
                onChange={() => (isSingle ? selectInSingleGroup(addOn.id) : onToggle(addOn.id))}
                className={cx(
                  "h-4 w-4 shrink-0 cursor-pointer border-line-strong accent-brand-600",
                  !isSingle && "rounded",
                )}
              />
              <span className="truncate text-fg">{addOn.name}</span>
            </span>
            <span className="nums shrink-0 text-fg-muted">+{inr(addOn.price)}</span>
          </motion.label>
        );
      })}
    </fieldset>
  );
}

/* -------------------------------------------------------------------------- */
/* Layout helpers                                                             */
/* -------------------------------------------------------------------------- */

/**
 * Commit affordance that is inline on desktop and pinned to the bottom of the
 * viewport on mobile, where the summary column stacks below a long form and
 * the primary action would otherwise be several screens down.
 *
 * It renders its children exactly once — a second, separately-rendered mobile
 * button would duplicate every accessible name on the page and make "the Pay
 * button" ambiguous to both screen readers and tests. Pair it with
 * `STICKY_BAR_SPACER` on the page's `main` so the bar never covers content at
 * the end of a scroll.
 */
export function StickyActionBar({ children }: { children: ReactNode }) {
  return (
    <div
      className={cx(
        "fixed inset-x-0 bottom-0 z-40 flex flex-col gap-2 border-t border-line bg-surface/95 px-4 py-3 shadow-lg backdrop-blur",
        "supports-[padding:max(0px)]:pb-[max(0.75rem,env(safe-area-inset-bottom))]",
        "md:static md:z-auto md:flex-col md:gap-3 md:border-0 md:bg-transparent md:p-0 md:shadow-none md:backdrop-blur-none",
      )}
    >
      {children}
    </div>
  );
}

/** Bottom padding a page needs so `StickyActionBar` never covers its last row. */
export const STICKY_BAR_SPACER = "pb-40 md:pb-10";

/**
 * Breadcrumb for a full-bleed `PageBanner` header - white text over the
 * banner's scrim, the same markup shape `categories/[slug]` and
 * `services/[slug]` each hand-rolled locally before this existed. The last
 * item (no `href`) renders as the current page, matching every other
 * breadcrumb in the app.
 */
export function BannerBreadcrumb({ items }: { items: { label: string; href?: string }[] }) {
  return (
    <nav aria-label="Breadcrumb" className="text-sm">
      <ol className="flex flex-wrap items-center gap-1.5 text-white/70">
        {items.map((item, index) => (
          <Fragment key={item.label}>
            {index > 0 ? <li aria-hidden>/</li> : null}
            <li>
              {item.href ? (
                <Link href={item.href} className="hover:text-white">
                  {item.label}
                </Link>
              ) : (
                <span className="truncate font-medium text-white" aria-current="page">
                  {item.label}
                </span>
              )}
            </li>
          </Fragment>
        ))}
      </ol>
    </nav>
  );
}

/**
 * Sidebar navigation card for the account section's 8/4 page layouts
 * (profile, bookings, ...): the same destinations `SiteHeader`'s own
 * account menu offers, reused rather than kept as a second, driftable copy.
 * `currentHref` drops that one entry - a link to the page it's already
 * rendered on serves nothing.
 */
export function AccountQuickLinksCard({ currentHref }: { currentHref: string }) {
  const links = useVisibleAccountLinks().filter((link) => link.href !== currentHref);

  return (
    <Card title="Manage your account">
      <nav aria-label="Account sections" className="-mx-2 flex flex-col">
        {links.map((link) => (
          <Link
            key={link.href}
            href={link.href}
            className="rounded-lg px-2 py-2 text-sm text-fg-muted transition-colors duration-fast ease-out hover:bg-surface-2 hover:text-fg"
          >
            {link.label}
          </Link>
        ))}
      </nav>
    </Card>
  );
}

/** Label/value row for the description lists on the detail and account screens. */
export function DetailRow({
  label,
  children,
  numeric = false,
}: {
  label: string;
  children: ReactNode;
  numeric?: boolean;
}) {
  return (
    <div className="flex items-baseline justify-between gap-4 text-sm">
      <dt className="shrink-0 text-fg-muted">{label}</dt>
      <dd className={cx("min-w-0 text-right font-medium text-fg", numeric && "nums")}>{children}</dd>
    </div>
  );
}

export function DetailList({ children }: { children: ReactNode }) {
  return <dl className="flex flex-col gap-2.5">{children}</dl>;
}

/**
 * Standard "this screen is loading" placeholder: a heading block plus a
 * configurable number of card-shaped blocks, sized to the real cards so the
 * page does not jump when the data lands.
 */
export function ScreenSkeleton({
  cards = 3,
  className = "mx-auto w-full max-w-3xl px-4 py-8 sm:px-6 sm:py-12",
  children,
}: {
  cards?: number;
  className?: string;
  /**
   * Optional live-region line rendered above the placeholder — for the cases
   * where the wait has a reason worth announcing (a redirect in flight), which
   * the silent `aria-hidden` blocks below cannot carry on their own.
   */
  children?: ReactNode;
}) {
  return (
    <main className={className}>
      {children ? <div className="mb-6">{children}</div> : null}
      <Skeleton className="h-8 w-56" />
      <Skeleton className="mt-3 h-4 w-72" />
      <div className="mt-8 flex flex-col gap-4">
        {Array.from({ length: cards }, (_, index) => (
          <Skeleton key={index} className="h-32 rounded-2xl" />
        ))}
      </div>
    </main>
  );
}

/* -------------------------------------------------------------------------- */
/* Status timeline (task 279 - extracted off the booking detail page so the   */
/* live tracking screen (task 281) can reuse the same rail)                   */
/* -------------------------------------------------------------------------- */

/**
 * Vertical status rail. The last recorded transition is the booking's current
 * state, so it carries the filled marker; everything before it is history.
 *
 * `providerAssignmentStatus` is appended as a live node rather than folded
 * into the history list: it is not a BookingStatusHistory row (it tracks the
 * separate BookingProviderAssignment entity, task 208) and it has no
 * changed-at of its own, so presenting it as a dated history entry would be
 * inventing data.
 */
export function Timeline({
  entries,
  currentStatus,
  providerAssignmentStatus,
}: {
  entries: BookingStatusTimelineEntry[];
  currentStatus: BookingStatus;
  providerAssignmentStatus: BookingProviderAssignmentStatus | null;
}) {
  const hasAssignment = providerAssignmentStatus !== null;
  const progress = professionalProgress(currentStatus, providerAssignmentStatus);

  if (entries.length === 0 && !hasAssignment) {
    return <p className="text-sm text-fg-muted">No status history yet.</p>;
  }

  const lastIndex = entries.length - 1;

  return (
    <ol className="flex flex-col">
      {entries.map((entry, index) => {
        const isLast = index === lastIndex;
        const isCurrent = isLast && !hasAssignment;
        return (
          <TimelineNode
            key={`${entry.toStatus}-${entry.changedAtUtc}-${index}`}
            tone={bookingStatusTone(entry.toStatus)}
            filled={isLast}
            isCurrent={isCurrent}
            showRail={!isLast || hasAssignment}
            title={entry.toStatusLabel}
            meta={formatInstant(entry.changedAtUtc)}
          >
            {entry.reason ? <p className="mt-1 text-sm text-fg-muted">{entry.reason}</p> : null}
          </TimelineNode>
        );
      })}

      {hasAssignment ? (
        <TimelineNode
          tone={providerAssignmentTone(providerAssignmentStatus)}
          filled
          isCurrent
          showRail={false}
          title={progress?.label ?? providerAssignmentLabel(providerAssignmentStatus)}
          meta="Professional assignment"
        >
          <p className="mt-1 text-sm text-fg-muted">
            {progress
              ? progress.detail
              : providerAssignmentStatus === BookingProviderAssignmentStatus.Accepted
                ? "Your professional has confirmed and will arrive in your slot window."
                : "This updates on its own — no action needed from you."}
          </p>
        </TimelineNode>
      ) : null}

      {/* A booking sitting in a status with no recorded history at all would
          otherwise render an empty rail. */}
      {entries.length === 0 && hasAssignment ? (
        <li className="sr-only">Current status: {currentStatus}</li>
      ) : null}
    </ol>
  );
}

const TIMELINE_NODE_TONES = {
  neutral: "bg-surface-3 text-fg-subtle ring-line",
  brand: "bg-brand-600 text-fg-on-brand ring-brand-600/25",
  success: "bg-success text-bg ring-success/25",
  warning: "bg-warning text-bg ring-warning/25",
  danger: "bg-danger text-bg ring-danger/25",
  info: "bg-info text-bg ring-info/25",
  accent: "bg-accent-500 text-bg ring-accent-500/25",
} as const;

export function TimelineNode({
  tone,
  filled,
  isCurrent,
  showRail,
  title,
  meta,
  children,
}: {
  tone: keyof typeof TIMELINE_NODE_TONES;
  filled: boolean;
  isCurrent: boolean;
  showRail: boolean;
  title: string;
  meta: string;
  children?: ReactNode;
}) {
  return (
    <li className="flex gap-3">
      <div className="flex flex-col items-center">
        <span
          aria-hidden
          className={cx(
            "mt-1 h-3 w-3 shrink-0 rounded-full ring-4",
            filled ? TIMELINE_NODE_TONES[tone] : "bg-line-strong ring-transparent",
            isCurrent && "animate-pop",
          )}
        />
        {showRail ? <span className="w-px flex-1 bg-line" /> : null}
      </div>
      <div className={cx("min-w-0 flex-1", showRail ? "pb-5" : "pb-0")}>
        <p className="text-sm font-medium text-fg">
          {title}
          {isCurrent ? <span className="sr-only"> (current)</span> : null}
        </p>
        <p className="mt-0.5 text-xs text-fg-subtle">{meta}</p>
        {children}
      </div>
    </li>
  );
}
