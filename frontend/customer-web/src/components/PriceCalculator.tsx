"use client";

import { useQuery } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { Alert, Button, Skeleton, cx } from "@/components/ui";
import { AddOnGroupSelector, VariantPicker } from "@/components/patterns";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import type {
  PriceBreakdown,
  PriceCalculationRequest,
  ServiceAddOnGroupSummary,
  ServiceAddOnSummary,
  ServiceVariantSummary,
} from "@/lib/types";

/**
 * Live, server-calculated price breakdown (SRS 11.6.1, 11.9 - "final price
 * must be calculated server-side", task 48). Quantity, variant and add-on
 * selections are local UI state; every change re-requests the authoritative
 * total rather than computing it client-side.
 *
 * `variants`/`addOnGroups` default to empty (Phase 3 catalog redesign) - a
 * service with neither renders and behaves exactly as it did before those
 * fields existed: no variant picker, no grouped selectors, just the flat
 * ungrouped add-on list.
 */
export function PriceCalculator({
  serviceId,
  addOns,
  cityId,
  variants = [],
  addOnGroups = [],
}: {
  serviceId: string;
  addOns: ServiceAddOnSummary[];
  cityId: string | null;
  variants?: ServiceVariantSummary[];
  addOnGroups?: ServiceAddOnGroupSummary[];
}) {
  const [quantity, setQuantity] = useState(1);
  const [selectedAddOnIds, setSelectedAddOnIds] = useState<Set<string>>(new Set());
  const [selectedVariantId, setSelectedVariantId] = useState<string | null>(variants[0]?.id ?? null);

  const toggleAddOn = (id: string) => {
    setSelectedAddOnIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  const request: PriceCalculationRequest | null = useMemo(
    () =>
      cityId
        ? {
            serviceId,
            cityId,
            quantity,
            // Sorted so the request (and therefore the React Query key) is
            // stable for a given selection. A raw Set iterates in insertion
            // order, so picking the same two add-ons in the opposite order
            // produced a different key and an avoidable extra pricing call.
            addOns: Array.from(selectedAddOnIds)
              .sort()
              .map((addOnId) => ({ addOnId, quantity: 1 })),
            serviceVariantId: selectedVariantId,
          }
        : null,
    [cityId, serviceId, quantity, selectedAddOnIds, selectedVariantId],
  );

  const query = useQuery({
    queryKey: ["price-calculation", request],
    queryFn: () =>
      apiFetch<PriceBreakdown>(`${API_V1}/pricing/calculate`, {
        method: "POST",
        body: JSON.stringify(request),
      }),
    enabled: request !== null,
    // Holds the previous total on screen while a new one is calculated, so
    // the price doesn't blink out every time a checkbox is ticked.
    placeholderData: (previous) => previous,
  });

  return (
    <div className="flex flex-col gap-5 rounded-lg border-2 border-line bg-surface p-5 shadow-sm">
      <VariantPicker variants={variants} selectedId={selectedVariantId} onSelect={setSelectedVariantId} />

      {/* A group, not a label: the value is a <span>, and htmlFor on a
          non-labelable element leaves the control unlabelled. */}
      <div
        role="group"
        aria-label="Quantity"
        className="flex items-center justify-between gap-3"
      >
        <span className="text-sm font-medium text-fg">Quantity</span>
        <div className="flex items-center gap-1">
          <StepperButton
            label="Decrease quantity"
            onClick={() => setQuantity((q) => Math.max(1, q - 1))}
            disabled={quantity <= 1}
          >
            −
          </StepperButton>
          <span className="nums w-8 text-center text-sm font-medium text-fg" aria-live="polite">
            {quantity}
          </span>
          <StepperButton label="Increase quantity" onClick={() => setQuantity((q) => q + 1)}>
            +
          </StepperButton>
        </div>
      </div>

      {addOnGroups.map((group) => (
        <AddOnGroupSelector key={group.id} group={group} selectedIds={selectedAddOnIds} onToggle={toggleAddOn} />
      ))}

      {addOns.length > 0 ? (
        <fieldset className="flex flex-col gap-2">
          <legend className="mb-1 text-sm font-medium text-fg">Add-ons</legend>
          {addOns.map((addOn) => {
            const checked = selectedAddOnIds.has(addOn.id);
            return (
              <label
                key={addOn.id}
                className={cx(
                  "flex cursor-pointer items-center justify-between gap-3 rounded-lg border px-3 py-2 text-sm transition duration-fast ease-out",
                  checked
                    ? "border-brand-600/40 bg-brand-50 dark:bg-brand-500/10"
                    : "border-line hover:border-line-strong hover:bg-surface-2",
                )}
              >
                <span className="flex min-w-0 items-center gap-2.5">
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => toggleAddOn(addOn.id)}
                    className="h-4 w-4 shrink-0 cursor-pointer rounded border-line-strong accent-brand-600"
                  />
                  <span className="truncate text-fg">{addOn.name}</span>
                </span>
                <span className="nums shrink-0 text-fg-muted">+₹{addOn.price}</span>
              </label>
            );
          })}
        </fieldset>
      ) : null}

      <div className="border-t border-line pt-4">
        {cityId === null ? (
          <p className="text-sm text-fg-muted">Select your city to see the total price.</p>
        ) : query.isPending ? (
          <div className="flex flex-col gap-2">
            <Skeleton className="h-3.5 w-full" />
            <Skeleton className="h-3.5 w-2/3" />
            <Skeleton className="mt-2 h-6 w-32" />
          </div>
        ) : query.isError ? (
          <Alert
            tone="error"
            action={
              <Button size="sm" variant="secondary" onClick={() => query.refetch()}>
                Retry
              </Button>
            }
          >
            {describeError(query.error)}
          </Alert>
        ) : (
          <div className={cx(query.isPlaceholderData && "opacity-60 transition-opacity")}>
            <PriceSummary breakdown={query.data} />
          </div>
        )}
      </div>
    </div>
  );
}

function StepperButton({
  label,
  onClick,
  disabled,
  children,
}: {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      onClick={onClick}
      disabled={disabled}
      className="flex h-8 w-8 items-center justify-center rounded-lg border border-line bg-surface text-base leading-none text-fg transition duration-fast ease-out hover:border-line-strong hover:bg-surface-2 disabled:cursor-not-allowed disabled:opacity-45"
    >
      {children}
    </button>
  );
}

function PriceSummary({ breakdown }: { breakdown: PriceBreakdown }) {
  return (
    <dl className="flex flex-col gap-2 text-sm">
      <Row label={`Base price × ${breakdown.quantity}`} value={breakdown.baseTotal} />
      {breakdown.addOnLineItems.map((item) => (
        <Row key={item.addOnId} label={`${item.name} × ${item.quantity}`} value={item.lineTotal} />
      ))}
      {breakdown.visitCharge > 0 ? <Row label="Visit charge" value={breakdown.visitCharge} /> : null}
      <Row label={`Tax (${breakdown.taxPercentage}%)`} value={breakdown.taxAmount} />
      {breakdown.platformFee > 0 ? <Row label="Platform fee" value={breakdown.platformFee} /> : null}

      {/* The stamped total line — the one number the customer is agreeing
          to pay, set apart with the same border-2/mono treatment as the
          rest of the ledger's "open page" language. */}
      <div className="mt-1 flex items-baseline justify-between rounded-md border-2 border-brand-600/40 bg-brand-50 px-3 py-2.5 dark:bg-brand-500/10">
        <dt className="font-display text-xs uppercase tracking-wide text-fg">Total payable</dt>
        <dd className="nums font-mono text-lg font-semibold text-brand-700 dark:text-brand-300">
          ₹{breakdown.totalPayable.toFixed(2)}
        </dd>
      </div>
    </dl>
  );
}

function Row({ label, value }: { label: string; value: number }) {
  return (
    <div className="flex items-baseline justify-between gap-3 text-fg-muted">
      <dt className="min-w-0 truncate">{label}</dt>
      <dd className="nums font-mono shrink-0">₹{value.toFixed(2)}</dd>
    </div>
  );
}
