"use client";

import type { UseQueryResult } from "@tanstack/react-query";
import Link from "next/link";
import { Alert, Button, EmptyState, LinkButton, Skeleton, cx } from "@/components/ui";
import { describeError } from "@/lib/api";
import type { CustomerAddress } from "@/lib/types";

/**
 * The customer's saved addresses as a single choice, used by both checkouts
 * (one-time booking and monthly cook). Addresses outside the area being booked
 * are flagged rather than hidden, so the customer sees why theirs can't be
 * used. "Add a new address" is a detour to /addresses/new that comes back to
 * `addHref`'s returnTo with ?newAddressId=... for the caller to select.
 */
export function AddressPicker({
  query,
  selectedId,
  onSelect,
  isOutside,
  areaName,
  addHref,
}: {
  query: UseQueryResult<CustomerAddress[]>;
  selectedId: string | null;
  onSelect: (addressId: string) => void;
  isOutside: (address: CustomerAddress) => boolean;
  /** Where the booking is for, e.g. the header's locality or the plan's city. */
  areaName: string;
  addHref: string;
}) {
  if (query.isPending) {
    return (
      <div className="flex flex-col gap-2" aria-hidden>
        <Skeleton className="h-[4.5rem] rounded-xl" />
        <Skeleton className="h-[4.5rem] rounded-xl" />
      </div>
    );
  }

  if (query.isError) {
    return (
      <Alert
        tone="error"
        title="Couldn't load your addresses"
        action={
          <Button size="sm" variant="secondary" onClick={() => query.refetch()}>
            Retry
          </Button>
        }
      >
        {describeError(query.error)}
      </Alert>
    );
  }

  if (query.data.length === 0) {
    return (
      <EmptyState
        title="No saved addresses"
        description="Add the address you'd like this service delivered to."
        action={<LinkButton href={addHref}>Add an address</LinkButton>}
      />
    );
  }

  return (
    <div className="flex flex-col gap-2">
      {query.data.every(isOutside) ? (
        <p className="text-sm leading-relaxed text-fg-muted">
          None of your saved addresses is in {areaName}. Add one there, or change your location at the
          top of the page.
        </p>
      ) : null}
      {query.data.map((address) => {
        const isSelected = address.id === selectedId;
        const isOutsideArea = isOutside(address);
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
              onChange={() => onSelect(address.id)}
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
                    Outside {areaName}
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
                  Pick an address in {areaName}, or change your location at the top of the
                  page.
                </span>
              ) : null}
            </span>
          </label>
        );
      })}
      <Link
        href={addHref}
        className="mt-1 inline-flex w-fit items-center gap-1 text-sm font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
      >
        + Add a new address
      </Link>
    </div>
  );
}
