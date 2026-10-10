"use client";

import { useQuery } from "@tanstack/react-query";
import { useRouter, useParams } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { CitySelector } from "@/components/CitySelector";
import { LocalitySelector } from "@/components/LocalitySelector";
import { RequireAuth } from "@/components/RequireAuth";
import {
  BannerBreadcrumb,
  STICKY_BAR_SPACER,
  ScreenSkeleton,
  StickyActionBar,
} from "@/components/patterns";
import { PageBanner } from "@/components/PageBanner";
import { SlotPicker } from "@/components/SlotPicker";
import {
  Alert,
  Button,
  Card,
  EmptyState,
  LinkButton,
  Select,
  Skeleton,
  cx,
} from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { getMyAmcContract, redeemAmcContractVisit } from "@/lib/amc-api";
import { todayIsoDate } from "@/lib/date";
import type { CategoryDetail, CategorySummary, CustomerAddress } from "@/lib/types";

/**
 * Redeem one visit against an AMC contract (docs/AMC.md): the same
 * service/address/slot selection an ordinary booking uses, minus payment -
 * the visit is already paid for. Deliberately narrower than the full
 * `/booking/summary` cart: no add-ons, variants or coupon, since the
 * resulting booking is zero-priced regardless of what's picked (there is
 * nothing to discount or upsell on an entitlement redemption). Reuses
 * `SlotPicker`, the same component the ordinary booking flow uses, rather
 * than a second slot-availability implementation.
 */
export default function RedeemAmcVisitPage() {
  return (
    <RequireAuth>
      <RedeemScreen />
    </RequireAuth>
  );
}

function RedeemScreen() {
  const { id: contractId } = useParams<{ id: string }>();
  const router = useRouter();
  const { city, locality } = useSelectedCity();

  const [selectedCategorySlug, setSelectedCategorySlug] = useState<string | null>(null);
  const [selectedServiceId, setSelectedServiceId] = useState<string | null>(null);
  const [selectedAddressId, setSelectedAddressId] = useState<string | null>(null);
  const [selectedDate, setSelectedDate] = useState<string>(todayIsoDate);
  const [selectedSlotWindowId, setSelectedSlotWindowId] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const inFlight = useRef(false);

  const contractQuery = useQuery({
    queryKey: ["amc-contract", contractId],
    queryFn: () => getMyAmcContract(contractId),
  });

  const categoriesQuery = useQuery({
    queryKey: ["categories", city?.id],
    queryFn: () => apiFetch<CategorySummary[]>(`${API_V1}/categories?cityId=${city?.id}`),
    enabled: !!city,
  });

  // Default to the contract's own category once both it and the city's
  // category list have loaded - a customer covering "AC" should not have to
  // re-find "AC" in a dropdown of everything Nestly sells. Falls back to
  // leaving nothing selected (the picker below) if no exact name match is
  // found, which can legitimately happen if the category was renamed since
  // this contract was purchased (MyAmcContractResponse only carries the
  // plan-time snapshot name, not a live category id).
  useEffect(() => {
    if (selectedCategorySlug || !contractQuery.data || !categoriesQuery.data) return;
    const match = categoriesQuery.data.find(
      (category) => category.name.toLowerCase() === contractQuery.data.categoryName.toLowerCase(),
    );
    // Reacting to two async queries arriving, not a render-time prop change.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    if (match) setSelectedCategorySlug(match.slug);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [contractQuery.data, categoriesQuery.data]);

  const categoryDetailQuery = useQuery({
    queryKey: ["category", selectedCategorySlug],
    queryFn: () => apiFetch<CategoryDetail>(`${API_V1}/categories/${selectedCategorySlug}`),
    enabled: !!selectedCategorySlug,
  });

  const services =
    categoryDetailQuery.data === undefined
      ? []
      : [
          ...categoryDetailQuery.data.services,
          ...categoryDetailQuery.data.serviceGroups.flatMap((group) => group.services),
        ];

  useEffect(() => {
    if (selectedServiceId || services.length === 0) return;
    // Reacting to the category query arriving, not a render-time prop change.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setSelectedServiceId(services[0].id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [services.length]);

  const addressesQuery = useQuery({
    queryKey: ["addresses"],
    queryFn: () => apiFetch<CustomerAddress[]>(`${API_V1}/addresses`, { authenticated: true }),
  });

  useEffect(() => {
    if (selectedAddressId !== null || !addressesQuery.data) return;
    const preferred = addressesQuery.data.find((a) => a.isDefault) ?? addressesQuery.data[0];
    // Reacting to the addresses query arriving, not a render-time prop change.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    if (preferred) setSelectedAddressId(preferred.id);
  }, [addressesQuery.data, selectedAddressId]);

  const handleRedeem = async () => {
    if (inFlight.current) return;
    if (!city || !locality || !selectedServiceId || !selectedAddressId || !selectedSlotWindowId) return;

    inFlight.current = true;
    setSubmitError(null);
    setIsSubmitting(true);

    try {
      const booking = await redeemAmcContractVisit(contractId, {
        serviceId: selectedServiceId,
        cityId: city.id,
        addressId: selectedAddressId,
        localityId: locality.id,
        slotWindowId: selectedSlotWindowId,
        slotDate: selectedDate,
        quantity: 1,
        addOns: [],
      });
      router.push(`/bookings/${booking.id}`);
    } catch (err) {
      setSubmitError(describeError(err));
      inFlight.current = false;
      setIsSubmitting(false);
    }
  };

  if (contractQuery.isPending) {
    return (
      <main className="flex w-full flex-col" aria-hidden>
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" />
        <ScreenSkeleton cards={3} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  if (contractQuery.isError) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-12 sm:px-6">
        <Alert
          tone="error"
          title="Couldn't load this contract"
          action={
            <Button size="sm" variant="secondary" onClick={() => contractQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(contractQuery.error)}
        </Alert>
      </main>
    );
  }

  const contract = contractQuery.data;

  if (!contract.canRedeemNow) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-12 sm:px-6">
        <EmptyState
          title="This contract can't redeem a visit right now"
          description="It may have run out of visits, expired, or been cancelled."
          action={<LinkButton href={`/amc/${contractId}`}>Back to contract</LinkButton>}
        />
      </main>
    );
  }

  const categoryOptions = (categoriesQuery.data ?? []).map((category) => ({
    value: category.slug,
    label: category.name,
  }));
  const serviceOptions = services.map((service) => ({ value: service.id, label: service.name }));
  const addressOptions = (addressesQuery.data ?? []).map((address) => ({
    value: address.id,
    label: `${address.label} — ${address.line1}, ${address.city}`,
  }));

  const canSubmit =
    !!city && !!locality && !!selectedServiceId && !!selectedAddressId && !!selectedSlotWindowId;

  return (
    <main className="flex w-full flex-col animate-rise">
      <PageBanner
        title="Redeem a visit"
        description={`${contract.assetLabel} — ${contract.visitsRemaining} of ${contract.visitsIncluded} visits left`}
        breadcrumb={
          <BannerBreadcrumb
            items={[
              { label: "Home", href: "/" },
              { label: "My AMC contracts", href: "/amc" },
              { label: contract.assetLabel, href: `/amc/${contractId}` },
              { label: "Redeem a visit" },
            ]}
          />
        }
      />

      <div className={cx("mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14", STICKY_BAR_SPACER)}>
      <div className="flex flex-col gap-6">
        <Card title="Service" description="Which service is this visit for?">
          {categoriesQuery.isPending ? (
            <Skeleton className="h-10 rounded-lg" />
          ) : categoriesQuery.isError ? (
            <Alert tone="error">{describeError(categoriesQuery.error)}</Alert>
          ) : (
            <div className="flex flex-col gap-4">
              <Select
                label="Category"
                options={[{ value: "", label: "Choose a category" }, ...categoryOptions]}
                value={selectedCategorySlug ?? ""}
                onChange={(event) => {
                  setSelectedCategorySlug(event.target.value || null);
                  setSelectedServiceId(null);
                }}
              />
              {selectedCategorySlug ? (
                categoryDetailQuery.isPending ? (
                  <Skeleton className="h-10 rounded-lg" />
                ) : categoryDetailQuery.isError ? (
                  <Alert tone="error">{describeError(categoryDetailQuery.error)}</Alert>
                ) : serviceOptions.length === 0 ? (
                  <p className="text-sm text-fg-subtle">No services under this category in your city.</p>
                ) : (
                  <Select
                    label="Service"
                    options={serviceOptions}
                    value={selectedServiceId ?? ""}
                    onChange={(event) => setSelectedServiceId(event.target.value || null)}
                  />
                )
              ) : null}
            </div>
          )}
        </Card>

        <Card title="Service address" description="Where should we send your professional?">
          {addressesQuery.isPending ? (
            <Skeleton className="h-10 rounded-lg" />
          ) : addressesQuery.isError ? (
            <Alert tone="error">{describeError(addressesQuery.error)}</Alert>
          ) : addressOptions.length === 0 ? (
            <EmptyState
              title="No saved addresses"
              description="Add an address to redeem a visit."
              action={<LinkButton href="/addresses/new">Add an address</LinkButton>}
            />
          ) : (
            <Select
              label="Address"
              options={addressOptions}
              value={selectedAddressId ?? ""}
              onChange={(event) => setSelectedAddressId(event.target.value || null)}
            />
          )}
        </Card>

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
          ) : selectedServiceId ? (
            <SlotPicker
              serviceId={selectedServiceId}
              localityId={locality.id}
              selectedDate={selectedDate}
              onDateChange={setSelectedDate}
              selectedSlotWindowId={selectedSlotWindowId}
              onSlotChange={(id) => setSelectedSlotWindowId(id)}
            />
          ) : (
            <p className="text-sm text-fg-subtle">Choose a service first to see available slots.</p>
          )}
        </Card>

        {submitError ? (
          <Alert tone="error" title="Couldn't redeem this visit">
            {submitError}
          </Alert>
        ) : null}
      </div>

      <StickyActionBar>
        <Button
          type="button"
          size="lg"
          fullWidth
          loading={isSubmitting}
          disabled={!canSubmit}
          onClick={handleRedeem}
        >
          Confirm redemption
        </Button>
      </StickyActionBar>
      </div>
    </main>
  );
}
