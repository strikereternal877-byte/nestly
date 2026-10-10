"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useState, type ComponentProps, type ReactNode } from "react";
import {
  Controller,
  useForm,
  type FieldValues,
  type Path,
  type Resolver,
  type UseFormReturn,
} from "react-hook-form";
import { z } from "zod";
import { Alert, Button, Card, CheckboxField, cx, EmptyState, Field, PageHeading, Skeleton, useToast } from "@/components/ui";
import { FormActions, FormGrid } from "@/components/data-table";
import { SectionError } from "@/components/screen-states";
import { apiFetch, describeError } from "@/lib/api";
import { canWriteModule } from "@/lib/permissions";
import {
  BOOKING_SEARCH_TERMS,
  CANCELLATION_SEARCH_TERMS,
  COUPON_SEARCH_TERMS,
  COUPONS_ENABLED_DESCRIPTION,
  CUSTOMER_FLAGS,
  FEATURE_SEARCH_TERMS,
  PLATFORM_FLAGS,
  PROVIDER_FLAGS,
  RESCHEDULE_SEARCH_TERMS,
  SLOT_SEARCH_TERMS,
  TAX_SEARCH_TERMS,
  WALLET_SEARCH_TERMS,
  hasAnySettingsMatch,
  matchesSearch,
  type SettingsSearchTerms,
} from "@/lib/settings-search-terms";
import { useAdminClaims } from "@/lib/use-admin-claims";
import type {
  AllSystemSettingsResponse,
  BookingSettings,
  CancellationSettings,
  CouponSettings,
  FeatureFlagSettings,
  RescheduleSettings,
  SlotSettings,
  TaxSettings,
  WalletSettings,
} from "@/lib/settings-types";

/**
 * System configuration / feature-flag management (SRS 12.19, tasks
 * 131a-131h): one card per settings group, each independently readable and
 * editable. Gated server-side behind "settings.read"/"settings.write"
 * (SystemSettingsController) - like every other admin page, this route's own
 * rendering is not the security boundary (see RequireAdminAuth's doc
 * comment); a 403 from the API surfaces as an inline alert per card.
 */
const SETTINGS_PATH = "/api/v1/settings";

/** react-hook-form's `register(name, { setValueAs })` helper for an optional numeric field: blank input means "unlimited"/"never" (null), matching the backend's nullable-cap convention. */
function emptyStringToNull(value: string): number | null {
  return value === "" ? null : Number(value);
}

function nullableNumberToInputValue(value: number | null): string {
  return value === null || value === undefined ? "" : String(value);
}

/**
 * Nullable numeric setting (a cap that's "unlimited" when unset), as a
 * `Controller`-bound field rather than `register(name, { setValueAs })` on a
 * plain uncontrolled input.
 *
 * `setValueAs` only runs inside the `onChange` handler `register` generates
 * - it transforms a value the admin actively types, but a field they never
 * touch keeps whatever `defaultValue` rendered the input with: `""` for
 * every one of these fields today, since they're all currently unset
 * (`null`) in the database. `z.number().nullable()` rejects that empty
 * string as neither a number nor null, so submitting the group without
 * first touching this one unrelated field silently failed client
 * validation - the whole card (every other field in it too) never reached
 * the API, with only a one-line "Invalid input" under this field as any
 * sign why. `Controller` keeps the field's RHF-tracked value correctly
 * typed (`number | null`) from the first render, whether the admin edits it
 * or not, so the untouched/blank/"unlimited" case - the actual current
 * state of every field this wraps - validates and saves like any other.
 */
function NullableNumberField<T extends FieldValues>({
  form,
  name,
  label,
}: {
  form: UseFormReturn<T>;
  name: Path<T>;
  label: string;
}) {
  return (
    <Controller
      control={form.control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          type="number"
          value={nullableNumberToInputValue(field.value as number | null)}
          onChange={(event) => field.onChange(emptyStringToNull(event.target.value))}
          onBlur={field.onBlur}
          error={fieldState.error?.message}
        />
      )}
    />
  );
}

/** Same fix as {@link NullableNumberField}, for the one nullable *text* setting (tax registration number). */
function NullableTextField<T extends FieldValues>({
  form,
  name,
  label,
}: {
  form: UseFormReturn<T>;
  name: Path<T>;
  label: string;
}) {
  return (
    <Controller
      control={form.control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          type="text"
          value={(field.value as string | null) ?? ""}
          onChange={(event) => field.onChange(event.target.value === "" ? null : event.target.value)}
          onBlur={field.onBlur}
          error={fieldState.error?.message}
        />
      )}
    />
  );
}

/**
 * A boolean setting spans the whole field grid. A boxed toggle carrying its
 * own explanation reads badly squeezed into one half-width column next to a
 * numeric input, and the feature flags here are the highest-consequence
 * controls on the page.
 *
 * `hidden` backs the on-page search box below: the row stays mounted (and
 * its `Controller` stays registered with react-hook-form) so filtering never
 * touches save/validation, it's just visually removed via CSS.
 */
function ToggleRow({ hidden, ...props }: ComponentProps<typeof CheckboxField> & { hidden?: boolean }) {
  return (
    <div className={cx("sm:col-span-2", hidden && "hidden")}>
      <CheckboxField {...props} />
    </div>
  );
}

/**
 * Wraps one non-toggle field (`Field`, `NullableNumberField`,
 * `NullableTextField`) so the search box can hide it without unmounting -
 * same reasoning as {@link ToggleRow}'s `hidden` prop, kept as a separate
 * wrapper here since those fields don't own their outer grid cell the way
 * `ToggleRow` does.
 */
function SearchableField({ hidden, children }: { hidden: boolean; children: ReactNode }) {
  return <div className={hidden ? "hidden" : undefined}>{children}</div>;
}

/**
 * `SettingsSearchTerms` (title/description/fields per group) lives in
 * `lib/settings-search-terms.ts` now, shared with the global command-palette
 * search - see that module's doc comment. Used two ways here: (1) each
 * group's own section component hides individual fields whose label doesn't
 * match, unless the *card's* title/description already matched (in which
 * case the whole card is shown unfiltered - see `getSearchVisibility`
 * below); (2) the page aggregates every group's terms (`hasAnySettingsMatch`)
 * to render the shared "no matching settings" empty state when nothing
 * anywhere matches.
 */
function getSearchVisibility(query: string, terms: SettingsSearchTerms) {
  const titleMatches = matchesSearch(query, terms.title, terms.description);
  const cardVisible = titleMatches || matchesSearch(query, ...terms.fields);
  return {
    cardVisible,
    // Once the card matched on its own title/description, showing only a
    // subset of its fields would be confusing (an admin who typed "wallet"
    // to find the Wallet settings card should see the whole card, not just
    // whichever field happens to contain "wallet"). Field-level filtering
    // only kicks in when the card is being shown *because* some field(s)
    // matched.
    isFieldVisible: (label: string) => titleMatches || matchesSearch(query, label),
  };
}

function useAllSettings() {
  return useQuery({
    queryKey: ["settings", "all"] as const,
    queryFn: () => apiFetch<AllSystemSettingsResponse>(SETTINGS_PATH, { authenticated: true }),
  });
}

function updateSettingsCache<K extends keyof AllSystemSettingsResponse>(
  queryClient: QueryClient,
  groupKey: K,
  value: AllSystemSettingsResponse[K],
) {
  queryClient.setQueryData<AllSystemSettingsResponse>(["settings", "all"], (current) =>
    current ? { ...current, [groupKey]: value } : current,
  );
}

/**
 * Shared card/form/save-state scaffolding for one settings group. The field
 * inputs themselves (which differ per group) are supplied via `children` as
 * a render-prop over the group's own `react-hook-form` instance - keeps the
 * seven groups from needing seven near-identical copies of the submit/error/
 * success wiring, while still letting each group's field list stay a plain,
 * explicit list rather than a generic metadata-driven one.
 */
function SettingsGroupCard<T extends FieldValues>({
  title,
  description,
  groupPath,
  schema,
  defaultValues,
  onSaved,
  canWrite,
  children,
}: {
  title: string;
  description: string;
  groupPath: string;
  schema: z.ZodType<T>;
  defaultValues: T;
  onSaved: (value: T) => void;
  /** Gates every field and the submit button - settings.write, checked once at the page level. */
  canWrite: boolean;
  children: (form: UseFormReturn<T>) => ReactNode;
}) {
  const toast = useToast();
  const [error, setError] = useState<string | null>(null);
  const form = useForm<T>({
    resolver: zodResolver(schema as never) as unknown as Resolver<T>,
    defaultValues: defaultValues as never,
  });

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    try {
      const updated = await apiFetch<T>(`${SETTINGS_PATH}/${groupPath}`, {
        method: "PUT",
        authenticated: true,
        body: JSON.stringify(values),
      });
      // `reset` to the server's echo, not the submitted values: it clears the
      // dirty state and makes the card show what was actually persisted.
      form.reset(updated);
      onSaved(updated);
      // A toast rather than a persistent inline alert. The old "Saved." alert
      // had no way to clear itself, so it stayed under a card the admin had
      // since edited again, asserting a save that had not happened.
      toast("success", `${title} saved.`);
    } catch (err) {
      // The form keeps its values, so a rejected save can be corrected and
      // resubmitted rather than retyped.
      setError(describeError(err));
    }
  });

  return (
    <Card
      title={title}
      description={canWrite ? description : "Read-only — you do not hold settings write access."}
    >
      <form onSubmit={onSubmit} className="flex flex-col gap-5" noValidate>
        {error ? <Alert>{error}</Alert> : null}
        {/* A native <fieldset disabled> cascades to every descendant input/
            select/button in one place, rather than threading a `disabled`
            prop into each of the seven groups' own differently-shaped field
            lists (plain Fields, Controller-wrapped nullable fields, toggle
            rows) - this page previously had no write-gating at all, unlike
            every other module screen in this app. */}
        <fieldset disabled={!canWrite} className="contents">
          <FormGrid columns={2}>{children(form)}</FormGrid>
          {canWrite ? (
            <FormActions>
              <Button type="submit" loading={form.formState.isSubmitting}>
                Save changes
              </Button>
            </FormActions>
          ) : null}
        </fieldset>
      </form>
    </Card>
  );
}

const bookingSchema = z.object({
  minLeadTimeHours: z.number().int().min(0).max(720),
  maxAdvanceBookingDays: z.number().int().min(1).max(365),
  maxActiveBookingsPerCustomer: z.number().int().min(1).max(1000).nullable(),
  allowSameDayBooking: z.boolean(),
});

function BookingSettingsSection({
  initial,
  queryClient,
  canWrite,
  query,
}: {
  initial: BookingSettings;
  queryClient: QueryClient;
  canWrite: boolean;
  query: string;
}) {
  const { cardVisible, isFieldVisible } = getSearchVisibility(query, BOOKING_SEARCH_TERMS);
  if (!cardVisible) return null;

  return (
    <SettingsGroupCard<BookingSettings>
      title={BOOKING_SEARCH_TERMS.title}
      description={BOOKING_SEARCH_TERMS.description}
      groupPath="booking"
      schema={bookingSchema}
      defaultValues={initial}
      onSaved={(value) => updateSettingsCache(queryClient, "booking", value)}
      canWrite={canWrite}
    >
      {(form) => (
        <>
          <SearchableField hidden={!isFieldVisible("Minimum lead time (hours)")}>
            <Field
              label="Minimum lead time (hours)"
              type="number"
              error={form.formState.errors.minLeadTimeHours?.message}
              {...form.register("minLeadTimeHours", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Max advance booking (days)")}>
            <Field
              label="Max advance booking (days)"
              type="number"
              error={form.formState.errors.maxAdvanceBookingDays?.message}
              {...form.register("maxAdvanceBookingDays", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Max active bookings per customer (blank = unlimited)")}>
            <NullableNumberField
              form={form}
              name="maxActiveBookingsPerCustomer"
              label="Max active bookings per customer (blank = unlimited)"
            />
          </SearchableField>
          <Controller
            control={form.control}
            name="allowSameDayBooking"
            render={({ field }) => (
              <ToggleRow
                label="Allow same-day booking"
                checked={field.value}
                onChange={field.onChange}
                hidden={!isFieldVisible("Allow same-day booking")}
              />
            )}
          />
        </>
      )}
    </SettingsGroupCard>
  );
}

const slotSchema = z.object({
  defaultSlotDurationMinutes: z.number().int().min(15).max(480),
  sameDayCutoffHours: z.number().int().min(0).max(24),
  maxAdvanceBookingDays: z.number().int().min(1).max(365),
  defaultSlotCapacity: z.number().int().min(1).max(1000),
  allowOverbooking: z.boolean(),
});

function SlotSettingsSection({
  initial,
  queryClient,
  canWrite,
  query,
}: {
  initial: SlotSettings;
  queryClient: QueryClient;
  canWrite: boolean;
  query: string;
}) {
  const { cardVisible, isFieldVisible } = getSearchVisibility(query, SLOT_SEARCH_TERMS);
  if (!cardVisible) return null;

  return (
    <SettingsGroupCard<SlotSettings>
      title={SLOT_SEARCH_TERMS.title}
      description={SLOT_SEARCH_TERMS.description}
      groupPath="slot"
      schema={slotSchema}
      defaultValues={initial}
      onSaved={(value) => updateSettingsCache(queryClient, "slot", value)}
      canWrite={canWrite}
    >
      {(form) => (
        <>
          <SearchableField hidden={!isFieldVisible("Default slot duration (minutes)")}>
            <Field
              label="Default slot duration (minutes)"
              type="number"
              error={form.formState.errors.defaultSlotDurationMinutes?.message}
              {...form.register("defaultSlotDurationMinutes", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Same-day cutoff (hours)")}>
            <Field
              label="Same-day cutoff (hours)"
              type="number"
              error={form.formState.errors.sameDayCutoffHours?.message}
              {...form.register("sameDayCutoffHours", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Max advance booking (days)")}>
            <Field
              label="Max advance booking (days)"
              type="number"
              error={form.formState.errors.maxAdvanceBookingDays?.message}
              {...form.register("maxAdvanceBookingDays", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Default slot capacity")}>
            <Field
              label="Default slot capacity"
              type="number"
              error={form.formState.errors.defaultSlotCapacity?.message}
              {...form.register("defaultSlotCapacity", { valueAsNumber: true })}
            />
          </SearchableField>
          <Controller
            control={form.control}
            name="allowOverbooking"
            render={({ field }) => (
              <ToggleRow
                label="Allow overbooking"
                checked={field.value}
                onChange={field.onChange}
                hidden={!isFieldVisible("Allow overbooking")}
              />
            )}
          />
        </>
      )}
    </SettingsGroupCard>
  );
}

const cancellationSchema = z.object({
  freeCancellationWindowHours: z.number().min(0).max(720),
  lateCancellationFeePercentage: z.number().min(0).max(100),
  allowAdminOverride: z.boolean(),
});

function CancellationSettingsSection({
  initial,
  queryClient,
  canWrite,
  query,
}: {
  initial: CancellationSettings;
  queryClient: QueryClient;
  canWrite: boolean;
  query: string;
}) {
  const { cardVisible, isFieldVisible } = getSearchVisibility(query, CANCELLATION_SEARCH_TERMS);
  if (!cardVisible) return null;

  return (
    <SettingsGroupCard<CancellationSettings>
      title={CANCELLATION_SEARCH_TERMS.title}
      description={CANCELLATION_SEARCH_TERMS.description}
      groupPath="cancellation"
      schema={cancellationSchema}
      defaultValues={initial}
      onSaved={(value) => updateSettingsCache(queryClient, "cancellation", value)}
      canWrite={canWrite}
    >
      {(form) => (
        <>
          <SearchableField hidden={!isFieldVisible("Free cancellation window (hours)")}>
            <Field
              label="Free cancellation window (hours)"
              type="number"
              step="0.5"
              error={form.formState.errors.freeCancellationWindowHours?.message}
              {...form.register("freeCancellationWindowHours", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Late cancellation fee (%)")}>
            <Field
              label="Late cancellation fee (%)"
              type="number"
              step="0.5"
              error={form.formState.errors.lateCancellationFeePercentage?.message}
              {...form.register("lateCancellationFeePercentage", { valueAsNumber: true })}
            />
          </SearchableField>
          <Controller
            control={form.control}
            name="allowAdminOverride"
            render={({ field }) => (
              <ToggleRow
                label="Allow admin override of the late fee"
                checked={field.value}
                onChange={field.onChange}
                hidden={!isFieldVisible("Allow admin override of the late fee")}
              />
            )}
          />
        </>
      )}
    </SettingsGroupCard>
  );
}

const rescheduleSchema = z.object({
  minHoursBeforeSlot: z.number().min(0).max(720),
  maxReschedulesPerBooking: z.number().int().min(0).max(50),
  lateFeeThresholdHours: z.number().min(0).max(720),
  lateRescheduleFeePercentage: z.number().min(0).max(100),
  collectLateFeeFromWallet: z.boolean(),
});

function RescheduleSettingsSection({
  initial,
  queryClient,
  canWrite,
  query,
}: {
  initial: RescheduleSettings;
  queryClient: QueryClient;
  canWrite: boolean;
  query: string;
}) {
  const { cardVisible, isFieldVisible } = getSearchVisibility(query, RESCHEDULE_SEARCH_TERMS);
  if (!cardVisible) return null;

  return (
    <SettingsGroupCard<RescheduleSettings>
      title={RESCHEDULE_SEARCH_TERMS.title}
      description={RESCHEDULE_SEARCH_TERMS.description}
      groupPath="reschedule"
      schema={rescheduleSchema}
      defaultValues={initial}
      onSaved={(value) => updateSettingsCache(queryClient, "reschedule", value)}
      canWrite={canWrite}
    >
      {(form) => (
        <>
          <SearchableField hidden={!isFieldVisible("Blocked within (hours before slot)")}>
            <Field
              label="Blocked within (hours before slot)"
              type="number"
              step="0.5"
              error={form.formState.errors.minHoursBeforeSlot?.message}
              {...form.register("minHoursBeforeSlot", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Max reschedules per booking")}>
            <Field
              label="Max reschedules per booking"
              type="number"
              error={form.formState.errors.maxReschedulesPerBooking?.message}
              {...form.register("maxReschedulesPerBooking", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Late fee threshold (hours before slot)")}>
            <Field
              label="Late fee threshold (hours before slot)"
              type="number"
              step="0.5"
              error={form.formState.errors.lateFeeThresholdHours?.message}
              {...form.register("lateFeeThresholdHours", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Late reschedule fee (%)")}>
            <Field
              label="Late reschedule fee (%)"
              type="number"
              step="0.5"
              error={form.formState.errors.lateRescheduleFeePercentage?.message}
              {...form.register("lateRescheduleFeePercentage", { valueAsNumber: true })}
            />
          </SearchableField>
          <Controller
            control={form.control}
            name="collectLateFeeFromWallet"
            render={({ field }) => (
              <ToggleRow
                label="Collect the late fee from the customer's wallet"
                description="Off: a late reschedule is only recorded, nothing is charged and no wallet is needed. On: the fee is debited from the customer's wallet, and a customer whose wallet cannot cover it cannot reschedule at that time (they can add money, if top-ups are on, or cancel). Turn this on once customers can add money."
                checked={field.value}
                onChange={field.onChange}
                hidden={!isFieldVisible("Collect the late fee from the customer's wallet")}
              />
            )}
          />
        </>
      )}
    </SettingsGroupCard>
  );
}

const taxSchema = z.object({
  defaultTaxPercentage: z.number().min(0).max(100),
  taxRegistrationNumber: z.string().max(50).nullable(),
  taxInclusivePricing: z.boolean(),
});

function TaxSettingsSection({
  initial,
  queryClient,
  canWrite,
  query,
}: {
  initial: TaxSettings;
  queryClient: QueryClient;
  canWrite: boolean;
  query: string;
}) {
  const { cardVisible, isFieldVisible } = getSearchVisibility(query, TAX_SEARCH_TERMS);
  if (!cardVisible) return null;

  return (
    <SettingsGroupCard<TaxSettings>
      title={TAX_SEARCH_TERMS.title}
      description={TAX_SEARCH_TERMS.description}
      groupPath="tax"
      schema={taxSchema}
      defaultValues={initial}
      onSaved={(value) => updateSettingsCache(queryClient, "tax", value)}
      canWrite={canWrite}
    >
      {(form) => (
        <>
          <SearchableField hidden={!isFieldVisible("Default tax rate (%)")}>
            <Field
              label="Default tax rate (%)"
              type="number"
              step="0.01"
              error={form.formState.errors.defaultTaxPercentage?.message}
              {...form.register("defaultTaxPercentage", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Tax registration number (blank = not configured)")}>
            <NullableTextField
              form={form}
              name="taxRegistrationNumber"
              label="Tax registration number (blank = not configured)"
            />
          </SearchableField>
          <Controller
            control={form.control}
            name="taxInclusivePricing"
            render={({ field }) => (
              <ToggleRow
                label="Displayed prices already include tax"
                checked={field.value}
                onChange={field.onChange}
                hidden={!isFieldVisible("Displayed prices already include tax")}
              />
            )}
          />
        </>
      )}
    </SettingsGroupCard>
  );
}

const walletSchema = z.object({
  maxWalletBalance: z.number().min(0),
  maxWalletUsagePercentagePerBooking: z.number().min(0).max(100),
  walletCreditExpiryDays: z.number().int().min(1).max(3650).nullable(),
  allowWalletTopUp: z.boolean(),
});

function WalletSettingsSection({
  initial,
  queryClient,
  canWrite,
  query,
}: {
  initial: WalletSettings;
  queryClient: QueryClient;
  canWrite: boolean;
  query: string;
}) {
  const { cardVisible, isFieldVisible } = getSearchVisibility(query, WALLET_SEARCH_TERMS);
  if (!cardVisible) return null;

  return (
    <SettingsGroupCard<WalletSettings>
      title={WALLET_SEARCH_TERMS.title}
      description={WALLET_SEARCH_TERMS.description}
      groupPath="wallet"
      schema={walletSchema}
      defaultValues={initial}
      onSaved={(value) => updateSettingsCache(queryClient, "wallet", value)}
      canWrite={canWrite}
    >
      {(form) => (
        <>
          <SearchableField hidden={!isFieldVisible("Max wallet balance")}>
            <Field
              label="Max wallet balance"
              type="number"
              step="0.01"
              error={form.formState.errors.maxWalletBalance?.message}
              {...form.register("maxWalletBalance", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Max wallet usage per booking (%)")}>
            <Field
              label="Max wallet usage per booking (%)"
              type="number"
              step="0.5"
              error={form.formState.errors.maxWalletUsagePercentagePerBooking?.message}
              {...form.register("maxWalletUsagePercentagePerBooking", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Wallet credit expiry (days, blank = never)")}>
            <NullableNumberField
              form={form}
              name="walletCreditExpiryDays"
              label="Wallet credit expiry (days, blank = never)"
            />
          </SearchableField>
          <Controller
            control={form.control}
            name="allowWalletTopUp"
            render={({ field }) => (
              <ToggleRow
                label="Allow customers to top up their wallet directly"
                checked={field.value}
                onChange={field.onChange}
                hidden={!isFieldVisible("Allow customers to top up their wallet directly")}
              />
            )}
          />
        </>
      )}
    </SettingsGroupCard>
  );
}

const couponSchema = z.object({
  maxDiscountPercentagePerCoupon: z.number().min(0).max(100),
  maxActiveCouponsPerCustomer: z.number().int().min(1).max(100).nullable(),
  allowCouponStacking: z.boolean(),
  couponsEnabled: z.boolean(),
});

function CouponSettingsSection({
  initial,
  queryClient,
  canWrite,
  query,
}: {
  initial: CouponSettings;
  queryClient: QueryClient;
  canWrite: boolean;
  query: string;
}) {
  const { cardVisible, isFieldVisible } = getSearchVisibility(query, COUPON_SEARCH_TERMS);
  if (!cardVisible) return null;

  return (
    <SettingsGroupCard<CouponSettings>
      title={COUPON_SEARCH_TERMS.title}
      description={COUPON_SEARCH_TERMS.description}
      groupPath="coupon"
      schema={couponSchema}
      defaultValues={initial}
      onSaved={(value) => updateSettingsCache(queryClient, "coupon", value)}
      canWrite={canWrite}
    >
      {(form) => (
        <>
          <SearchableField hidden={!isFieldVisible("Max discount per coupon (%)")}>
            <Field
              label="Max discount per coupon (%)"
              type="number"
              step="0.5"
              error={form.formState.errors.maxDiscountPercentagePerCoupon?.message}
              {...form.register("maxDiscountPercentagePerCoupon", { valueAsNumber: true })}
            />
          </SearchableField>
          <SearchableField hidden={!isFieldVisible("Max active coupons per customer (blank = unlimited)")}>
            <NullableNumberField
              form={form}
              name="maxActiveCouponsPerCustomer"
              label="Max active coupons per customer (blank = unlimited)"
            />
          </SearchableField>
          <Controller
            control={form.control}
            name="allowCouponStacking"
            render={({ field }) => (
              <ToggleRow
                label="Allow more than one coupon per booking"
                checked={field.value}
                onChange={field.onChange}
                hidden={!isFieldVisible("Allow more than one coupon per booking")}
              />
            )}
          />
          <Controller
            control={form.control}
            name="couponsEnabled"
            render={({ field }) => (
              <ToggleRow
                label="Coupons enabled"
                description={COUPONS_ENABLED_DESCRIPTION}
                checked={field.value}
                onChange={field.onChange}
                hidden={!isFieldVisible(`Coupons enabled ${COUPONS_ENABLED_DESCRIPTION}`)}
              />
            )}
          />
        </>
      )}
    </SettingsGroupCard>
  );
}

const featureSchema = z.object({
  walletEnabled: z.boolean(),
  referralsEnabled: z.boolean(),
  amcSubscriptionsEnabled: z.boolean(),
  serviceRatingsEnabled: z.boolean(),
  bookingHelpLinkEnabled: z.boolean(),
  ratingsPageEnabled: z.boolean(),
  calendarViewEnabled: z.boolean(),
  earningsLedgerEnabled: z.boolean(),
  offersScreenEnabled: z.boolean(),
  autoManageServiceabilityEnabled: z.boolean(),
});

/** Full-width subheading between the Customer/Provider flag groups within one FormGrid - same span as ToggleRow, so it lines up rather than sitting in a half-width column. */
function FlagGroupHeading({ children, hidden }: { children: ReactNode; hidden?: boolean }) {
  return (
    <p
      className={cx(
        "sm:col-span-2 mt-1 text-xs font-semibold uppercase tracking-wide text-fg-subtle first:mt-0",
        hidden && "hidden",
      )}
    >
      {children}
    </p>
  );
}

function FeatureFlagToggle({
  form,
  name,
  label,
  description,
  hidden,
}: {
  form: UseFormReturn<FeatureFlagSettings>;
  name: keyof FeatureFlagSettings;
  label: string;
  description: string;
  hidden: boolean;
}) {
  return (
    <Controller
      control={form.control}
      name={name}
      render={({ field }) => (
        <ToggleRow label={label} description={description} checked={field.value} onChange={field.onChange} hidden={hidden} />
      )}
    />
  );
}

function FeatureFlagSettingsSection({
  initial,
  queryClient,
  canWrite,
  query,
}: {
  initial: FeatureFlagSettings;
  queryClient: QueryClient;
  canWrite: boolean;
  query: string;
}) {
  const { cardVisible, isFieldVisible } = getSearchVisibility(query, FEATURE_SEARCH_TERMS);
  if (!cardVisible) return null;

  const isFlagVisible = (flag: { label: string; description: string }) =>
    isFieldVisible(`${flag.label} ${flag.description}`);
  const customerVisible = CUSTOMER_FLAGS.some(isFlagVisible);
  const providerVisible = PROVIDER_FLAGS.some(isFlagVisible);
  const platformVisible = PLATFORM_FLAGS.some(isFlagVisible);

  return (
    <SettingsGroupCard<FeatureFlagSettings>
      title={FEATURE_SEARCH_TERMS.title}
      description={FEATURE_SEARCH_TERMS.description}
      groupPath="features"
      schema={featureSchema}
      defaultValues={initial}
      onSaved={(value) => updateSettingsCache(queryClient, "feature", value)}
      canWrite={canWrite}
    >
      {(form) => (
        <>
          <FlagGroupHeading hidden={!customerVisible}>Customer app</FlagGroupHeading>
          {CUSTOMER_FLAGS.map((flag) => (
            <FeatureFlagToggle key={flag.name} form={form} {...flag} hidden={!isFlagVisible(flag)} />
          ))}

          <FlagGroupHeading hidden={!providerVisible}>Provider app</FlagGroupHeading>
          {PROVIDER_FLAGS.map((flag) => (
            <FeatureFlagToggle key={flag.name} form={form} {...flag} hidden={!isFlagVisible(flag)} />
          ))}

          <FlagGroupHeading hidden={!platformVisible}>Platform</FlagGroupHeading>
          {PLATFORM_FLAGS.map((flag) => (
            <FeatureFlagToggle key={flag.name} form={form} {...flag} hidden={!isFlagVisible(flag)} />
          ))}
        </>
      )}
    </SettingsGroupCard>
  );
}

/**
 * Shaped like a real settings card — header, a two-column field grid, a
 * button row — so the page does not reflow when the settings land.
 */
function SettingsCardSkeleton({ fields = 4 }: { fields?: number }) {
  return (
    <div className="rounded-2xl bg-surface p-6 shadow-sm">
      <Skeleton className="h-4 w-40" />
      <Skeleton className="mt-2 h-3.5 w-72 max-w-full" />
      <div className="mt-6 grid grid-cols-1 gap-4 sm:grid-cols-2">
        {Array.from({ length: fields }, (_, index) => (
          <div key={index} className="flex flex-col gap-2">
            <Skeleton className="h-3.5 w-40" />
            <Skeleton className="h-10 w-full" />
          </div>
        ))}
      </div>
      <div className="mt-5 flex justify-end">
        <Skeleton className="h-10 w-32" />
      </div>
    </div>
  );
}

export default function SystemSettingsPage() {
  const queryClient = useQueryClient();
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "settings");
  const { data, isPending, isError, error, refetch } = useAllSettings();
  // Client-side only: filters what's already rendered, across every group
  // and subgroup regardless of scroll position. No new endpoint - the whole
  // settings payload is already on the page.
  const [searchQuery, setSearchQuery] = useState("");
  const trimmedQuery = searchQuery.trim();
  const noMatches = trimmedQuery !== "" && !hasAnySettingsMatch(searchQuery);

  return (
    <div className="flex w-full max-w-4xl animate-rise flex-col gap-6">
      <PageHeading
        title="System settings"
        subtitle="Admin-configurable booking, slot, cancellation, reschedule, tax, wallet, coupon and feature-flag rules (SRS 12.19)."
      />

      {!isPending && !isError ? (
        <Field
          type="search"
          label="Search settings"
          placeholder="Search by setting name, e.g. “wallet”, “overbooking”, “tax rate”…"
          value={searchQuery}
          onChange={(event) => setSearchQuery(event.target.value)}
        />
      ) : null}

      {isPending ? (
        <>
          <SettingsCardSkeleton fields={4} />
          <SettingsCardSkeleton fields={5} />
          <SettingsCardSkeleton fields={3} />
          <SettingsCardSkeleton fields={9} />
        </>
      ) : isError ? (
        // Previously a bare Alert with no way out: a transient failure left
        // the whole screen empty until the admin reloaded the browser.
        <SectionError error={error} onRetry={() => void refetch()} />
      ) : noMatches ? (
        <EmptyState
          title="No matching settings"
          description={`No setting matched "${trimmedQuery}". Try a different search term.`}
        />
      ) : (
        <>
          <BookingSettingsSection initial={data.booking} queryClient={queryClient} canWrite={canWrite} query={searchQuery} />
          <SlotSettingsSection initial={data.slot} queryClient={queryClient} canWrite={canWrite} query={searchQuery} />
          <CancellationSettingsSection
            initial={data.cancellation}
            queryClient={queryClient}
            canWrite={canWrite}
            query={searchQuery}
          />
          <RescheduleSettingsSection
            initial={data.reschedule}
            queryClient={queryClient}
            canWrite={canWrite}
            query={searchQuery}
          />
          <TaxSettingsSection initial={data.tax} queryClient={queryClient} canWrite={canWrite} query={searchQuery} />
          <WalletSettingsSection initial={data.wallet} queryClient={queryClient} canWrite={canWrite} query={searchQuery} />
          <CouponSettingsSection initial={data.coupon} queryClient={queryClient} canWrite={canWrite} query={searchQuery} />
          <FeatureFlagSettingsSection
            initial={data.feature}
            queryClient={queryClient}
            canWrite={canWrite}
            query={searchQuery}
          />
        </>
      )}
    </div>
  );
}
