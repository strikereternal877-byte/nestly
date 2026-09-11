"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { BookingHelpLink } from "@/components/BookingHelpLink";
import { CitySelector } from "@/components/CitySelector";
import { LocalitySelector } from "@/components/LocalitySelector";
import { PageBanner } from "@/components/PageBanner";
import {
  AddOnGroupSelector,
  BannerBreadcrumb,
  BookingProgress,
  FrequencyPicker,
  OPTION_ROW,
  PriceBreakdownList,
  PriceBreakdownSkeleton,
  STICKY_BAR_SPACER,
  ScreenSkeleton,
  StickyActionBar,
  VariantPicker,
  formatCalendarDate,
  inr,
  recurringFrequencyLabel,
} from "@/components/patterns";
import { RequireAuth } from "@/components/RequireAuth";
import { SlotPicker } from "@/components/SlotPicker";
import {
  Alert,
  Button,
  Card,
  CheckboxField,
  EmptyState,
  Field,
  LinkButton,
  Skeleton,
  cx,
} from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, ApiError, apiFetch, describeError, errorCode } from "@/lib/api";
import { type BookingDraft, readDraft, writeDraft } from "@/lib/booking-draft";
import { addRecurrenceInterval, todayIsoDate } from "@/lib/date";
import { RecurringBookingRecurrenceFrequency } from "@/lib/types";
import type {
  BookingSummary,
  BookingSummaryRequestBody,
  CategoryDetail,
  CreateRecurringBookingPlanRequestBody,
  CustomerAddress,
  RecurringBookingPlanResponse,
  ServiceDetail,
  SlotRevalidation,
} from "@/lib/types";

/**
 * Largest quantity the "+" control will reach.
 *
 * Mirrors the server's Booking:MaxQuantityPerBooking, which is the authority
 * and rejects anything above it (Booking.QuantityTooLarge). Held here too so
 * the control simply stops rather than letting someone lean on it up to a
 * ₹74,950 booking for fifty cleans in one four-hour window and only finding
 * out at checkout. Keep the two in step if the server limit changes.
 */
const MAX_QUANTITY = 10;

/**
 * Booking summary / cart page (SRS 11.7, tasks 62a-f) with slot selection
 * folded in (SRS 11.8, tasks 63a-c) - both feed the same
 * BookingSummaryRequest, so splitting them into separate routes would just
 * mean passing the same half-built request back and forth through the URL.
 *
 * Single-service cart model only (SRS 11.7.1): the request always describes
 * exactly one service, matching BookingSummaryRequest's shape.
 *
 * Wrapped in Suspense: useSearchParams opts the tree below it out of static
 * rendering, and Next's App Router requires a Suspense boundary around that
 * or the production build fails (see search/page.tsx for the same pattern).
 */
export default function BookingSummaryPage() {
  return (
    <Suspense
      fallback={
        <main className="flex w-full flex-col">
          <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
          <ScreenSkeleton cards={4} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
        </main>
      }
    >
      <RequireAuth>
        <BookingSummaryScreen />
      </RequireAuth>
    </Suspense>
  );
}

function BookingSummaryScreen() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const serviceSlug = searchParams.get("serviceSlug");
  const { city, locality } = useSelectedCity();

  const [quantity, setQuantity] = useState(1);
  const [selectedAddOnIds, setSelectedAddOnIds] = useState<Set<string>>(new Set());
  const [selectedVariantId, setSelectedVariantId] = useState<string | null>(null);
  const [selectedAddressId, setSelectedAddressId] = useState<string | null>(null);
  const [selectedDate, setSelectedDate] = useState<string>(todayIsoDate);
  const [selectedSlotWindowId, setSelectedSlotWindowId] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  /**
   * "Repeat this booking" opt-in (task 298).
   *
   * The frequency picker lives on the booking flow itself rather than only on
   * the standalone /recurring-bookings/new form, because the moment a customer
   * is willing to commit to a standing visit is the moment they are already
   * booking one — sending them off to re-pick the same service, address and
   * slot on a second screen loses almost all of them.
   *
   * The booking being placed here is the plan's first visit and is *not* part
   * of the plan; the plan starts one full interval later (see
   * `recurringPlanStartDate`). `recurringPlanId` is set once the plan exists,
   * which both switches this card to a confirmation and stops a second plan
   * being created if the customer comes back and proceeds again.
   */
  const [repeatEnabled, setRepeatEnabled] = useState(false);
  const [repeatFrequency, setRepeatFrequency] = useState<RecurringBookingRecurrenceFrequency>(
    RecurringBookingRecurrenceFrequency.Weekly,
  );
  const [repeatCount, setRepeatCount] = useState("4");
  const [recurringPlanId, setRecurringPlanId] = useState<string | null>(null);
  const [recurringPlanError, setRecurringPlanError] = useState<string | null>(null);

  /**
   * A booking created from this draft that has not been paid for yet.
   *
   * Set the moment POST /bookings succeeds, so a customer who comes back here
   * - most often by pressing Back from the payment screen to re-check
   * something - is offered the booking they already have instead of silently
   * being given a second one while the first sits in PaymentPending holding a
   * slot.
   */
  const [pendingBookingId, setPendingBookingId] = useState<string | null>(null);

  /**
   * Double-submit guard. `isSubmitting` disables the button, but on a slow
   * connection a second click can land in the same tick as the first, before
   * React has re-rendered the disabled state - and a duplicate POST /bookings
   * creates a second real booking the customer then has to cancel. A ref is
   * checked and set synchronously, so the second call returns immediately.
   *
   * `navigated` keeps the guard closed for good once a booking exists. The
   * route transition to the payment screen is not instant, and on a slow
   * connection it is seconds long; re-enabling the button during it put an
   * impatient customer one tap away from a second real booking, which is
   * exactly what happened - three bookings from one intent, two of them
   * stranded in PaymentPending holding slot capacity.
   */
  const inFlight = useRef(false);
  const navigated = useRef(false);

  /**
   * Idempotency key (task 241). `inFlight`/`navigated` above stop a
   * synchronous double-click, but not a client-side retry after a timeout,
   * a flaky connection, or a second open tab - each of those is a genuinely
   * new call that the guards above don't see, and each used to be able to
   * create a second real booking from one intent. Keyed off the request's
   * own content: the same key is replayed for an identical retry (so the
   * backend recognises it and hands back the booking already created rather
   * than a second one), but a real edit - address, slot, quantity, coupon -
   * mints a fresh key, since that is honestly a different booking.
   */
  const idempotencyKeyRef = useRef<string | null>(null);
  const idempotencyRequestSignatureRef = useRef<string | null>(null);

  // Coupon (task 77, SRS 11.10.3). appliedCouponCode is the code the backend
  // has confirmed - couponInput is just the text box's draft value, kept
  // separate so a typo mid-edit doesn't silently change what's charged.
  const [couponInput, setCouponInput] = useState("");
  const [appliedCouponCode, setAppliedCouponCode] = useState<string | null>(null);
  const [couponMessage, setCouponMessage] = useState<string | null>(null);
  const [couponError, setCouponError] = useState<string | null>(null);
  const [isApplyingCoupon, setIsApplyingCoupon] = useState(false);

  // Wallet credit (task 310, SRS 11.7.2). A toggle, not a typed-in amount -
  // see BookingSummaryRequestBody.applyWalletCredit's doc comment. Off by
  // default: the /wallet page used to promise this happened automatically,
  // which was never true - now that it's real, it's an opt-in the customer
  // sees and can change their mind about, not a silent default.
  const [applyWalletCredit, setApplyWalletCredit] = useState(false);

  const serviceQuery = useQuery({
    queryKey: ["service", serviceSlug],
    queryFn: () => apiFetch<ServiceDetail>(`${API_V1}/services/${serviceSlug}`),
    enabled: !!serviceSlug,
  });

  // Checkout banner (SRS 11.5 + booking summary): `ServiceDetail` already
  // carries `categorySlug`, so this is the same category-detail fetch
  // `app/categories/[slug]/page.tsx` makes — just for its `pageBannerUrl`,
  // not the full page. Only enabled once the service has resolved.
  const categorySlug = serviceQuery.data?.categorySlug;
  const categoryQuery = useQuery({
    queryKey: ["category", categorySlug],
    queryFn: () => apiFetch<CategoryDetail>(`${API_V1}/categories/${categorySlug}`),
    enabled: !!categorySlug,
  });

  const addressesQuery = useQuery({
    queryKey: ["addresses"],
    queryFn: () => apiFetch<CustomerAddress[]>(`${API_V1}/addresses`, { authenticated: true }),
  });

  /**
   * Whether an address sits in the area currently being booked.
   *
   * The slots, the price and the serviceability check are all computed for the
   * locality chosen in the header, while the professional is sent to this
   * address - so an address in another area cannot be served by the slot being
   * booked. `pincodeId` is resolved server-side when the address is saved; an
   * address with none matched no pincode we serve at all. Unknown until the
   * locality has loaded, in which case nothing is flagged.
   */
  const isAddressInSelectedArea = (address: CustomerAddress): boolean =>
    !locality?.pincodeId || address.pincodeId === locality.pincodeId;

  // Default to the customer's default address once addresses load - but only
  // to one that can actually be served here. A default address in another city
  // is exactly what nobody re-reads before paying, and pre-selecting it walked
  // customers into a booking no provider could fulfil.
  useEffect(() => {
    if (selectedAddressId !== null || !addressesQuery.data) return;
    const serviceable = addressesQuery.data.filter(isAddressInSelectedArea);
    const preferred =
      serviceable.find((a) => a.isDefault) ??
      serviceable[0] ??
      addressesQuery.data.find((a) => a.isDefault) ??
      addressesQuery.data[0];
    if (preferred) setSelectedAddressId(preferred.id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [addressesQuery.data, selectedAddressId, locality?.pincodeId]);

  /**
   * Whether the restore below has run yet.
   *
   * The persist effect must not write until it has. Both effects fire in the
   * same commit on mount, and the restore's setState calls are only visible on
   * the *next* render - so persisting immediately wrote this page's initial
   * state (today's date, no slot, no pending booking) straight over the draft
   * it was about to restore from, and the customer's selections were gone by
   * the time the restored values came back. State rather than a ref precisely
   * because the persist effect has to re-run once the restore lands.
   */
  const [hasRestoredDraft, setHasRestoredDraft] = useState(false);

  // Restore a draft left by a detour off this page, or by pressing Back from
  // the payment screen - see the doc comment in lib/booking-draft.ts.
  // Deliberately once-per-serviceSlug, not on every render.
  useEffect(() => {
    if (!serviceSlug) return;
    const draft = readDraft(serviceSlug);
    if (draft) {
      setQuantity(draft.quantity);
      setSelectedAddOnIds(new Set(draft.addOnIds));
      setSelectedVariantId(draft.serviceVariantId ?? null);
      setSelectedAddressId(draft.addressId);
      setSelectedDate(draft.date);
      setSelectedSlotWindowId(draft.slotWindowId);
      setPendingBookingId(draft.pendingBookingId ?? null);
      setRepeatEnabled(draft.repeatEnabled ?? false);
      if (draft.repeatFrequency !== null && draft.repeatFrequency !== undefined) {
        setRepeatFrequency(draft.repeatFrequency as RecurringBookingRecurrenceFrequency);
      }
      if (draft.repeatCount) setRepeatCount(draft.repeatCount);
      setRecurringPlanId(draft.recurringPlanId ?? null);
    }
    setHasRestoredDraft(true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serviceSlug]);

  // Once the draft has had its say, default to the service's first variant
  // (Phase 3 catalog redesign) - a service with none leaves this null and
  // books at its flat price, unchanged from before variants existed.
  useEffect(() => {
    if (!hasRestoredDraft || selectedVariantId !== null || !serviceQuery.data) return;
    const firstVariant = serviceQuery.data.variants[0];
    if (firstVariant) setSelectedVariantId(firstVariant.id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hasRestoredDraft, serviceQuery.data]);

  // Resume from a "+ Add a new address" detour (see addNewAddressHref below):
  // addresses/new sends the customer back here with ?newAddressId=... so the
  // address they just entered is selected automatically instead of making
  // them find and pick it again from the list. Declared after the draft
  // restore above so it wins when both fire on the same mount - the draft
  // only remembers whatever was selected *before* the detour, which for a
  // customer who had no address yet is nothing at all.
  useEffect(() => {
    const newAddressId = searchParams.get("newAddressId");
    if (!newAddressId || !serviceSlug) return;
    setSelectedAddressId(newAddressId);
    const cleaned = new URLSearchParams(searchParams.toString());
    cleaned.delete("newAddressId");
    router.replace(`/booking/summary?${cleaned.toString()}`, { scroll: false });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const draftState: BookingDraft = {
    quantity,
    addOnIds: Array.from(selectedAddOnIds),
    serviceVariantId: selectedVariantId,
    addressId: selectedAddressId,
    date: selectedDate,
    slotWindowId: selectedSlotWindowId,
    pendingBookingId,
    repeatEnabled,
    repeatFrequency,
    repeatCount,
    recurringPlanId,
  };

  // Persist on every change so a detour (most commonly "+ Add a new address"
  // below) doesn't cost the customer their selections. Never before the
  // restore above has had its say - see hasRestoredDraft.
  useEffect(() => {
    if (!serviceSlug || !hasRestoredDraft) return;
    writeDraft(serviceSlug, {
      quantity,
      addOnIds: Array.from(selectedAddOnIds),
      serviceVariantId: selectedVariantId,
      addressId: selectedAddressId,
      date: selectedDate,
      slotWindowId: selectedSlotWindowId,
      pendingBookingId,
      repeatEnabled,
      repeatFrequency,
      repeatCount,
      recurringPlanId,
    });
  }, [
    serviceSlug,
    hasRestoredDraft,
    quantity,
    selectedAddOnIds,
    selectedVariantId,
    selectedAddressId,
    selectedDate,
    selectedSlotWindowId,
    pendingBookingId,
    repeatEnabled,
    repeatFrequency,
    repeatCount,
    recurringPlanId,
  ]);

  const toggleAddOn = (id: string) => {
    setSelectedAddOnIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const service = serviceQuery.data;
  const requestReady =
    !!service && !!city && !!locality && !!selectedAddressId && !!selectedSlotWindowId;

  const request: BookingSummaryRequestBody | null = requestReady
    ? {
        serviceId: service.id,
        cityId: city.id,
        addressId: selectedAddressId,
        localityId: locality.id,
        slotWindowId: selectedSlotWindowId,
        slotDate: selectedDate,
        quantity,
        addOns: Array.from(selectedAddOnIds, (addOnId) => ({ addOnId, quantity: 1 })),
        // Keeping the applied coupon on the shared request means every
        // recompute (quantity/add-on/slot changes) and the eventual booking
        // creation call all honour it consistently, instead of it being a
        // display-only side channel that gets dropped at checkout time.
        couponCode: appliedCouponCode,
        // Same reasoning as the coupon above: on the shared request so the
        // preview and the actual POST /bookings call always agree on
        // whether wallet credit applies.
        applyWalletCredit,
        serviceVariantId: selectedVariantId,
      }
    : null;

  /**
   * When the plan's first visit falls — one full interval after the booking
   * being placed here, so the plan never duplicates it. Shown to the customer
   * before they commit, because "repeat every month" is ambiguous about
   * whether the next visit is in a month or on the 1st.
   */
  const recurringPlanStartDate = addRecurrenceInterval(selectedDate, repeatFrequency);

  const repeatCountValue = Number(repeatCount.trim());
  const isRepeatCountValid =
    repeatCount.trim() !== "" && Number.isInteger(repeatCountValue) && repeatCountValue > 0;

  /**
   * The plan to create alongside the booking, or null if the booking request
   * isn't complete enough yet. Deliberately built from the same `request` the
   * booking is priced and created from, so the plan can never describe a
   * different service, address or slot than the booking it repeats.
   */
  const buildRecurringPlanBody = (
    bookingRequest: BookingSummaryRequestBody,
  ): CreateRecurringBookingPlanRequestBody => {
    const isMonthly = repeatFrequency === RecurringBookingRecurrenceFrequency.Monthly;
    const anchor = new Date(`${recurringPlanStartDate}T00:00:00`);

    return {
      serviceId: bookingRequest.serviceId,
      cityId: bookingRequest.cityId,
      addressId: bookingRequest.addressId,
      localityId: bookingRequest.localityId,
      slotWindowId: bookingRequest.slotWindowId,
      quantity: bookingRequest.quantity,
      frequency: repeatFrequency,
      recurrenceDayOfWeek: isMonthly ? null : anchor.getDay(),
      recurrenceDayOfMonth: isMonthly ? anchor.getDate() : null,
      startDate: recurringPlanStartDate,
      // Bounded by a visit count rather than an end date: "four more cleans"
      // is how customers describe this, and the plan aggregate requires one or
      // the other. The standalone /recurring-bookings/new form still offers an
      // end date for anyone who wants it.
      endDate: null,
      occurrenceCount: repeatCountValue,
      // No couponCode: a coupon is a one-time redemption and is deliberately
      // absent from CreateRecurringBookingPlanRequest - see its doc comment.
      addOns: bookingRequest.addOns,
    };
  };

  const summaryQuery = useQuery({
    queryKey: ["booking-summary", request],
    queryFn: () =>
      apiFetch<BookingSummary>(`${API_V1}/bookings/summary`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify(request),
      }),
    enabled: request !== null,
  });

  /**
   * A coupon that stops qualifying must not take the checkout down with it.
   *
   * The coupon rides on the shared summary request, so once the cart changes
   * under it - dropping quantity back below the coupon's minimum order value
   * is the classic case - every repricing call fails. The customer was left
   * with "Couldn't price this booking", a Retry button that could never
   * succeed, a permanently disabled "Proceed to book", and the coupon card
   * that caused it far below the fold. Dropping the coupon and saying so in
   * the coupon card keeps the booking priceable and puts the cause where the
   * customer can act on it.
   */
  useEffect(() => {
    const error = summaryQuery.error;
    if (!appliedCouponCode || !(error instanceof ApiError)) return;
    if (!errorCode(error)?.startsWith("Coupon.")) return;

    setAppliedCouponCode(null);
    setCouponMessage(null);
    setCouponError(
      `${appliedCouponCode} no longer applies to this booking and has been removed. ${describeError(error)}`,
    );
  }, [summaryQuery.error, appliedCouponCode]);

  const handleApplyCoupon = async () => {
    const code = couponInput.trim();
    if (!request || !code) return;
    setIsApplyingCoupon(true);
    setCouponError(null);
    setCouponMessage(null);

    try {
      const result = await apiFetch<BookingSummary>(`${API_V1}/coupons/apply`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ ...request, couponCode: code }),
      });
      setAppliedCouponCode(code);
      const discount = result.coupon?.discountAmount ?? 0;
      setCouponMessage(`${result.coupon?.code ?? code} applied — you saved ${inr(discount)}.`);
    } catch (err) {
      setCouponError(describeError(err));
    } finally {
      setIsApplyingCoupon(false);
    }
  };

  const handleRemoveCoupon = () => {
    setAppliedCouponCode(null);
    setCouponInput("");
    setCouponMessage(null);
    setCouponError(null);
  };

  const handleProceed = async () => {
    if (!request || !service) return;
    // Synchronous re-entry guard - see `inFlight` above.
    if (inFlight.current) return;

    setRecurringPlanError(null);
    if (repeatEnabled && !recurringPlanId && !isRepeatCountValid) {
      setRecurringPlanError("Tell us how many repeat visits you'd like — at least one.");
      return;
    }

    inFlight.current = true;

    setSubmitError(null);
    setIsSubmitting(true);

    try {
      // Re-check the selected slot right before booking (SRS 11.8.3, task
      // 63c) so a slot that went stale while the customer was reviewing the
      // summary fails with a clear, specific message instead of a generic
      // booking-creation error.
      const revalidation = await apiFetch<SlotRevalidation>(
        `${API_V1}/slots/revalidate?serviceId=${request.serviceId}&localityId=${request.localityId}&slotWindowId=${request.slotWindowId}&date=${request.slotDate}`,
      );

      if (!revalidation.isValid) {
        setSelectedSlotWindowId(null);
        setSubmitError(
          revalidation.reason ?? "This slot is no longer available. Please choose another.",
        );
        return;
      }

      const requestSignature = JSON.stringify(request);
      if (idempotencyRequestSignatureRef.current !== requestSignature) {
        idempotencyKeyRef.current = crypto.randomUUID();
        idempotencyRequestSignatureRef.current = requestSignature;
      }

      const booking = await apiFetch<{ id: string }>(`${API_V1}/bookings`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ ...request, idempotencyKey: idempotencyKeyRef.current }),
      });

      // The draft deliberately survives: the booking is created but not yet
      // paid, and a customer who goes Back from the payment screen to
      // re-check the address must find their selections intact rather than an
      // empty page. It carries the new booking's id from here on, so this
      // screen can offer to resume that unpaid booking instead of silently
      // creating a second one. Cleared for real once payment succeeds (see
      // booking/success).
      writeDraft(service.slug, { ...draftState, pendingBookingId: booking.id });
      setPendingBookingId(booking.id);

      /**
       * The standing plan, if the customer opted in above. Created after the
       * booking rather than before it, because the booking is the primary
       * intent and an orphaned plan for a booking that never happened is worse
       * than a booking with no plan.
       *
       * A failure here deliberately stops the journey on this page rather than
       * carrying on to payment: the booking is safe (it exists, and the
       * "waiting for payment" banner above offers it straight back), while the
       * repeat schedule is the part the customer would otherwise never learn
       * had silently not happened. Proceeding again replays the same
       * idempotency key, so the retry re-attempts the plan without creating a
       * second booking.
       */
      if (repeatEnabled && !recurringPlanId) {
        try {
          const plan = await apiFetch<RecurringBookingPlanResponse>(
            `${API_V1}/recurring-booking-plans`,
            {
              method: "POST",
              authenticated: true,
              body: JSON.stringify(buildRecurringPlanBody(request)),
            },
          );
          setRecurringPlanId(plan.id);
          writeDraft(service.slug, {
            ...draftState,
            pendingBookingId: booking.id,
            recurringPlanId: plan.id,
          });
        } catch (err) {
          setRecurringPlanError(describeError(err));
          return;
        }
      }

      navigated.current = true;
      router.push(`/booking/payment/${booking.id}?serviceSlug=${service.slug}`);
      return;
    } catch (err) {
      // Everything the customer entered - quantity, add-ons, address, slot,
      // coupon - is component state and is untouched by a failure, so a retry
      // costs one click and no re-entry.
      setSubmitError(describeError(err));
    } finally {
      // Once a booking exists the button stays busy through the route
      // transition and beyond - the customer's next action belongs on the
      // payment screen, not here.
      if (navigated.current) return;
      inFlight.current = false;
      setIsSubmitting(false);
    }
  };

  if (!serviceSlug) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-10">
        <EmptyState
          title="No service selected"
          description="Choose a service first and we'll bring you straight back here to review it."
          action={<LinkButton href="/categories">Browse services</LinkButton>}
        />
      </main>
    );
  }

  if (serviceQuery.isPending) {
    return (
      <main className="flex w-full flex-col">
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
        <ScreenSkeleton cards={4} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  if (serviceQuery.isError || !service) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-10">
        <Alert
          tone="error"
          title="Couldn't load this service"
          action={
            <Button size="sm" variant="secondary" onClick={() => serviceQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(serviceQuery.error)}
        </Alert>
      </main>
    );
  }

  const summary = summaryQuery.data;
  // finalPayable already folds in every discount that applies (coupon,
  // subscription benefit, wallet credit - task 310) - it equals
  // price.totalPayable whenever none of them do, so there's no need to
  // special-case "no coupon" here the way this used to.
  const payable = summary ? summary.finalPayable : null;

  // Carries this exact review page as the return trip for the "add a new
  // address" detour below - see the newAddressId effect above and
  // addresses/new/page.tsx's returnTo handling.
  const addNewAddressHref = `/addresses/new?returnTo=${encodeURIComponent(`/booking/summary?serviceSlug=${service.slug}`)}`;

  return (
    <main className="flex w-full flex-col animate-rise">
      <PageBanner
        title="Review your booking"
        description={service.name}
        imageUrl={categoryQuery.data?.pageBannerUrl}
        breadcrumb={
          <BannerBreadcrumb
            items={[
              { label: "Home", href: "/" },
              { label: service.categoryName, href: `/categories/${service.categorySlug}` },
              { label: "Review your booking" },
            ]}
          />
        }
      />

      <div
        className={cx(
          "mx-auto grid w-full max-w-7xl gap-6 px-4 py-10 sm:px-6 sm:py-14 md:grid-cols-[1fr_22rem]",
          STICKY_BAR_SPACER,
        )}
      >
      <div className="flex min-w-0 flex-col gap-6 md:col-start-1">
        <BookingProgress current={0} />

        {/* A booking from this draft already exists and is awaiting payment -
            offer it back rather than letting the customer unknowingly create a
            second one (they cannot tell two identical "Awaiting Payment" rows
            apart afterwards). */}
        {pendingBookingId ? (
          <Alert
            tone="info"
            title="You already have this booking waiting for payment"
            action={
              <Button
                size="sm"
                onClick={() =>
                  router.push(`/booking/payment/${pendingBookingId}?serviceSlug=${service.slug}`)
                }
              >
                Continue payment
              </Button>
            }
          >
            Booking {pendingBookingId.slice(0, 8)} hasn&apos;t been paid for yet. Continue where you
            left off, or change anything below and place a new booking instead.
          </Alert>
        ) : null}

        {/* Cart: service + add-ons (task 62a). */}
        <Card title="Service">
          <div className="flex items-baseline justify-between gap-4">
            <span className="text-sm font-medium text-fg">{service.name}</span>
            <span className="nums text-sm text-fg-muted">{inr(service.price)}</span>
          </div>

          {/* Quantity only for unit-measured services (AC units, rooms,
              seats). A flat-rate service stays at quantity 1 and hides the
              control - the server rejects any other quantity for it anyway
              (Booking.QuantityNotAllowed). */}
          {service.isQuantityAllowed ? (
            <div className="mt-4 flex items-center justify-between gap-4">
              <span className="text-sm font-medium text-fg">Quantity</span>
              <div className="flex items-center gap-2">
                <QuantityButton
                  label="Decrease quantity"
                  onClick={() => setQuantity((q) => Math.max(1, q - 1))}
                  disabled={quantity <= 1}
                >
                  −
                </QuantityButton>
                <span className="nums w-8 text-center text-sm font-medium text-fg" aria-live="polite">
                  {quantity}
                </span>
                <QuantityButton
                  label="Increase quantity"
                  onClick={() => setQuantity((q) => Math.min(MAX_QUANTITY, q + 1))}
                  disabled={quantity >= MAX_QUANTITY}
                >
                  +
                </QuantityButton>
              </div>
            </div>
          ) : null}

          {service.variants.length > 0 ? (
            <div className="mt-5 border-t border-line pt-4">
              <VariantPicker
                variants={service.variants}
                selectedId={selectedVariantId}
                onSelect={setSelectedVariantId}
              />
            </div>
          ) : null}

          {service.addOnGroups.map((group) => (
            <div key={group.id} className="mt-5 border-t border-line pt-4">
              <AddOnGroupSelector group={group} selectedIds={selectedAddOnIds} onToggle={toggleAddOn} />
            </div>
          ))}

          {service.addOns.length > 0 ? (
            <fieldset className="mt-5 border-t border-line pt-4">
              <legend className="mb-2.5 text-sm font-medium text-fg">Add-ons</legend>
              <div className="flex flex-col gap-2">
                {service.addOns.map((addOn) => {
                  const checked = selectedAddOnIds.has(addOn.id);
                  return (
                    <label key={addOn.id} className={OPTION_ROW(checked)}>
                      <span className="flex min-w-0 items-center gap-2.5">
                        <input
                          type="checkbox"
                          checked={checked}
                          onChange={() => toggleAddOn(addOn.id)}
                          className="h-4 w-4 shrink-0 cursor-pointer rounded border-line-strong accent-brand-600"
                        />
                        <span className="min-w-0 truncate text-fg">{addOn.name}</span>
                      </span>
                      <span className="nums shrink-0 text-fg-muted">+{inr(addOn.price)}</span>
                    </label>
                  );
                })}
              </div>
            </fieldset>
          ) : null}
        </Card>

        {/* Address selection (task 62b). */}
        <Card title="Service address" description="Where should we send your professional?">
          {addressesQuery.isPending ? (
            <div className="flex flex-col gap-2" aria-hidden>
              <Skeleton className="h-[4.5rem] rounded-xl" />
              <Skeleton className="h-[4.5rem] rounded-xl" />
            </div>
          ) : addressesQuery.isError ? (
            <Alert
              tone="error"
              title="Couldn't load your addresses"
              action={
                <Button size="sm" variant="secondary" onClick={() => addressesQuery.refetch()}>
                  Retry
                </Button>
              }
            >
              {describeError(addressesQuery.error)}
            </Alert>
          ) : addressesQuery.data.length === 0 ? (
            <EmptyState
              title="No saved addresses"
              description="Add the address you'd like this service delivered to."
              action={<LinkButton href={addNewAddressHref}>Add an address</LinkButton>}
            />
          ) : (
            <div className="flex flex-col gap-2">
              {addressesQuery.data.map((address) => {
                const isSelected = address.id === selectedAddressId;
                const isOutsideArea = !isAddressInSelectedArea(address);
                return (
                  <label
                    key={address.id}
                    className={cx(
                      "flex cursor-pointer items-start gap-3 rounded-xl border p-3.5 text-sm transition duration-fast ease-out",
                      isSelected
                        ? "border-brand-600 bg-brand-50 shadow-xs dark:bg-brand-500/10"
                        : "border-line bg-surface hover:border-line-strong hover:bg-surface-2",
                      isOutsideArea && !isSelected && "opacity-70",
                    )}
                  >
                    <input
                      type="radio"
                      name="address"
                      className="mt-0.5 h-4 w-4 shrink-0 cursor-pointer accent-brand-600"
                      checked={isSelected}
                      onChange={() => setSelectedAddressId(address.id)}
                    />
                    <span className="min-w-0">
                      <span className="flex items-center gap-2 font-medium text-fg">
                        {address.label}
                        {address.isDefault ? (
                          <span className="rounded-full bg-surface-3 px-2 py-0.5 text-xs font-medium text-fg-muted">
                            Default
                          </span>
                        ) : null}
                        {isOutsideArea ? (
                          <span className="rounded-full bg-warning-soft px-2 py-0.5 text-xs font-medium text-warning">
                            Outside {locality?.name ?? "this area"}
                          </span>
                        ) : null}
                      </span>
                      <span className="mt-0.5 block leading-relaxed text-fg-muted">
                        {address.line1}
                        {address.line2 ? `, ${address.line2}` : ""}, {address.city}{" "}
                        <span className="nums">{address.pincode}</span>
                      </span>
                      {isOutsideArea && isSelected ? (
                        <span className="mt-1.5 block text-xs leading-relaxed text-warning">
                          We can&apos;t serve this address from the area you&apos;re booking in.
                          Pick an address in {locality?.name ?? "this area"}, or change your
                          location at the top of the page.
                        </span>
                      ) : null}
                    </span>
                  </label>
                );
              })}
              <Link
                href={addNewAddressHref}
                className="mt-1 inline-flex w-fit items-center gap-1 text-sm font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
              >
                + Add a new address
              </Link>
            </div>
          )}
        </Card>

        {/* Slot selection (task 62c, 63a-c). */}
        <Card title="Slot" description="Pick a date and time window.">
          {city === undefined ? (
            <Skeleton className="h-24 rounded-xl" />
          ) : city === null ? (
            <div className="flex flex-col items-start gap-2.5">
              <p className="text-sm text-fg-muted">Select your city first.</p>
              <CitySelector />
            </div>
          ) : locality === null ? (
            <LocalitySelector cityId={city.id} />
          ) : (
            <SlotPicker
              serviceId={service.id}
              localityId={locality.id}
              selectedDate={selectedDate}
              onDateChange={setSelectedDate}
              selectedSlotWindowId={selectedSlotWindowId}
              onSlotChange={(id) => setSelectedSlotWindowId(id)}
            />
          )}
        </Card>

        {/* "Repeat this booking" opt-in (task 298). Sits directly under the
            slot it repeats, so the frequency and the day it lands on are read
            together. */}
        <Card
          title="Repeat this booking"
          description="Make it a standing visit and we'll book each one for you."
        >
          <div className="flex flex-col gap-4">
          {recurringPlanId ? (
            <Alert tone="success" title="Repeat schedule set up">
              We&apos;ll book {service.name} {recurringFrequencyLabel(repeatFrequency).toLowerCase()}{" "}
              from <span className="nums">{formatCalendarDate(recurringPlanStartDate)}</span>.{" "}
              <Link
                href="/recurring-bookings"
                className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
              >
                Manage it here
              </Link>
              .
            </Alert>
          ) : (
            <div className="flex flex-col gap-4">
              <CheckboxField
                label="Repeat this booking"
                description="The booking you're placing now stays exactly as it is — repeats are added on top, starting one interval later."
                checked={repeatEnabled}
                onChange={setRepeatEnabled}
              />

              {repeatEnabled ? (
                <div className="flex flex-col gap-4 border-t border-line pt-4">
                  <FrequencyPicker
                    label="Repeat frequency"
                    value={repeatFrequency}
                    onChange={setRepeatFrequency}
                  />

                  <Field
                    id="repeat-count"
                    label="Number of repeat visits"
                    type="number"
                    min={1}
                    className="max-w-[10rem]"
                    value={repeatCount}
                    onChange={(e) => setRepeatCount(e.target.value)}
                    hint="Not counting the booking you're placing now."
                  />

                  <p className="text-xs leading-relaxed text-fg-subtle">
                    First repeat visit:{" "}
                    <span className="nums font-medium text-fg-muted">
                      {formatCalendarDate(recurringPlanStartDate)}
                    </span>
                    , in the same time window. Each visit is booked and paid for on its own — pause
                    or cancel the schedule any time from{" "}
                    <Link
                      href="/recurring-bookings"
                      className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
                    >
                      Recurring bookings
                    </Link>
                    .
                  </p>
                </div>
              ) : null}
            </div>
          )}

          {recurringPlanError ? (
            <Alert tone="warning" title="Your booking is placed — the repeat schedule isn't">
              {recurringPlanError} Your booking is safe; press &ldquo;Proceed to book&rdquo; again to
              retry the schedule without creating a second booking, or continue to payment and set
              the schedule up later.
            </Alert>
          ) : null}
          </div>
        </Card>

        {/* Coupon (task 62d, 77; SRS 11.10.3). */}
        <Card title="Coupon" description="Have a code? Apply it before you pay.">
          <div className="flex flex-col gap-3">
            {appliedCouponCode ? (
              <div className="flex items-center justify-between gap-3 rounded-xl border border-success/25 bg-success-soft px-3.5 py-2.5">
                <span className="font-mono text-sm font-semibold uppercase tracking-wide text-success">
                  {appliedCouponCode}
                </span>
                <Button type="button" size="sm" variant="secondary" onClick={handleRemoveCoupon}>
                  Remove
                </Button>
              </div>
            ) : (
              <div className="flex gap-2">
                <div className="w-full">
                  <Field
                    type="text"
                    id="coupon-code"
                    label="Coupon code"
                    hideLabel
                    value={couponInput}
                    onChange={(e) => setCouponInput(e.target.value)}
                    placeholder="Enter coupon code"
                    className="uppercase placeholder:normal-case"
                  />
                </div>
                <Button
                  type="button"
                  variant="secondary"
                  loading={isApplyingCoupon}
                  disabled={!request || !couponInput.trim()}
                  onClick={handleApplyCoupon}
                >
                  Apply
                </Button>
              </div>
            )}
            {!appliedCouponCode && !request ? (
              <p className="text-xs text-fg-subtle">
                Choose an address and a slot first — a coupon is validated against the full booking.
              </p>
            ) : null}
            {couponMessage ? <Alert tone="success">{couponMessage}</Alert> : null}
            {couponError ? <Alert tone="error">{couponError}</Alert> : null}
          </div>
        </Card>

        {/* Wallet credit (task 310, SRS 11.7.2). Only shown once the summary
            has actually loaded and there is a balance to offer - a customer
            with nothing in their wallet has nothing to decide here. */}
        {summary && summary.wallet.balance > 0 ? (
          <Card title="Wallet credit" description="Use your Glavyx wallet balance towards this booking.">
            <CheckboxField
              label={`Use my wallet balance (${inr(summary.wallet.balance)} available)`}
              description={
                applyWalletCredit && summary.wallet.appliedAmount > 0
                  ? `${inr(summary.wallet.appliedAmount)} will be applied towards this booking.`
                  : "Applied after any coupon or subscription discount, up to what's still payable."
              }
              checked={applyWalletCredit}
              onChange={setApplyWalletCredit}
            />
          </Card>
        ) : null}
      </div>

      <aside className="flex flex-col gap-4 md:sticky md:top-20 md:col-start-2 md:row-start-1 md:self-start">
        {summaryQuery.isPending && request !== null ? (
          <Card title="Price summary">
            <PriceBreakdownSkeleton />
          </Card>
        ) : summaryQuery.isError ? (
          <Card title="Price summary">
            <Alert
              tone="error"
              title="Couldn't price this booking"
              action={
                <Button size="sm" variant="secondary" onClick={() => summaryQuery.refetch()}>
                  Retry
                </Button>
              }
            >
              {describeError(summaryQuery.error)}
            </Alert>
          </Card>
        ) : summary ? (
          <BookingSummaryCard summary={summary} />
        ) : (
          <Card title="Price summary">
            <p className="text-sm leading-relaxed text-fg-muted">
              Choose your address and slot to see the full price breakdown.
            </p>
          </Card>
        )}

        {submitError ? (
          <Alert
            tone="error"
            title="We couldn't place your booking"
            action={
              <Button size="sm" variant="secondary" onClick={handleProceed} disabled={!request}>
                Try again
              </Button>
            }
          >
            {submitError} Nothing you entered has been lost.
          </Alert>
        ) : null}

        <StickyActionBar>
          {payable !== null ? (
            <div className="flex items-baseline justify-between gap-3 md:hidden">
              <span className="text-xs font-medium uppercase tracking-wide text-fg-muted">
                Total payable
              </span>
              <span className="nums text-lg font-semibold text-fg">{inr(payable)}</span>
            </div>
          ) : null}

          <Button
            type="button"
            size="lg"
            fullWidth
            loading={isSubmitting}
            disabled={!summary}
            onClick={handleProceed}
          >
            Proceed to book
          </Button>

          {/* Announced rather than shown as a blocking overlay: the button's
              own spinner covers the visual case, this covers screen readers. */}
          <p role="status" aria-live="polite" className="sr-only">
            {isSubmitting ? "Placing your booking, please wait." : ""}
          </p>

        </StickyActionBar>

        {/* Task 187's standalone recurring-plan form. Now the *secondary* way
            in: the ordinary case is the "Repeat this booking" opt-in above,
            which keeps the customer in one flow, and this covers the case that
            opt-in deliberately doesn't - a schedule with no booking today, or
            one bounded by an end date rather than a visit count. Reworded from
            "Set up as recurring instead" so the two aren't read as rival ways
            to do the same thing. LinkButton rather than <Link><Button/></Link>:
            a button inside an anchor is invalid HTML and gives assistive tech
            two nested controls for one action. */}
        <LinkButton href={`/recurring-bookings/new?serviceSlug=${service.slug}`} variant="secondary" fullWidth>
          Schedule for later without booking now
        </LinkButton>

        <BookingHelpLink />
      </aside>
      </div>
    </main>
  );
}

function QuantityButton({
  label,
  onClick,
  disabled,
  children,
}: {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  children: ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      onClick={onClick}
      disabled={disabled}
      className="flex h-9 w-9 items-center justify-center rounded-lg border border-line bg-surface text-base text-fg shadow-xs transition duration-fast ease-out hover:border-line-strong hover:bg-surface-2 active:scale-[0.96] disabled:cursor-not-allowed disabled:opacity-45 disabled:active:scale-100"
    >
      {children}
    </button>
  );
}

/**
 * Price breakdown (task 62e) + policy summary (task 62f, SRS 11.7.2), with
 * the coupon discount, wallet credit (task 310) and recomputed final payable
 * folded in (task 77) - the discount lines and the total all visibly change
 * whenever a coupon or wallet credit is applied or removed, satisfying SRS
 * 11.10.3's "recompute" requirement.
 */
function BookingSummaryCard({ summary }: { summary: BookingSummary }) {
  return (
    <Card title="Price summary">
      <div className="flex flex-col gap-4">
        <PriceBreakdownList
          breakdown={summary.price}
          discount={
            summary.coupon
              ? { code: summary.coupon.code, amount: summary.coupon.discountAmount }
              : null
          }
          walletCreditApplied={summary.wallet.appliedAmount}
          total={summary.finalPayable}
        />

        {summary.coupon ? (
          <p className="rounded-lg bg-success-soft px-3 py-2 text-xs font-medium text-success">
            You saved <span className="nums">{inr(summary.coupon.discountAmount)}</span> with{" "}
            {summary.coupon.code}.
          </p>
        ) : null}

        {summary.cancellationPolicy || summary.reschedulePolicy ? (
          <div className="border-t border-line pt-3">
            <p className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-fg-subtle">
              Policy
            </p>
            <div className="flex flex-col gap-1 text-xs leading-relaxed text-fg-muted">
              {summary.cancellationPolicy ? <p>{summary.cancellationPolicy}</p> : null}
              {summary.reschedulePolicy ? <p>{summary.reschedulePolicy}</p> : null}
            </div>
          </div>
        ) : null}
      </div>
    </Card>
  );
}
