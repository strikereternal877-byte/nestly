"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import type { FocusEvent, ReactNode } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { StickyActionBar } from "@/components/patterns";
import { Alert, Button, Card, Checkbox, Field } from "@/components/ui";
import { API_V1, apiFetch } from "@/lib/api";
import type { CustomerAddress, PincodeLookup } from "@/lib/types";

/**
 * Shared by the add and edit screens — both post the same
 * UpsertAddressRequest shape (AddressContracts.cs), so the field list and its
 * validation live in one place.
 */
/**
 * Screens out obviously-junk address text before it ever reaches the API
 * (mirrors AddressValidators.cs' BeMeaningfulAddressLine - same three checks,
 * same "structural not semantic" scope: long enough, has real
 * letters/digits, isn't a mashed/placeholder run like "....." or "aaaaaa").
 * Real-but-meaningless filler ("kuchh ni") is a semantic problem no
 * client-side regex can catch without address verification, which is out of
 * scope here (see docs/OPEN-FIXES-FEATURES.csv "Address snapshot" row).
 */
const ADDRESS_LINE_MIN_LENGTH = 5;
const REPEATED_CHARACTER_RUN = /(.)\1{2,}/;

function isMeaningfulAddressLine(value: string): boolean {
  const trimmed = value.trim();
  if (trimmed.length < ADDRESS_LINE_MIN_LENGTH) return false;
  if (!/[a-zA-Z0-9]/.test(trimmed)) return false;
  return !REPEATED_CHARACTER_RUN.test(trimmed);
}

const ADDRESS_LINE_MESSAGE = `Enter at least ${ADDRESS_LINE_MIN_LENGTH} characters of real address content, not filler text.`;

export const addressSchema = z.object({
  label: z.string().min(1, "Label is required").max(50),
  line1: z
    .string()
    .min(1, "Address line 1 is required")
    .max(200)
    .refine(isMeaningfulAddressLine, ADDRESS_LINE_MESSAGE),
  line2: z
    .string()
    .max(200)
    .refine((value) => value === "" || isMeaningfulAddressLine(value), ADDRESS_LINE_MESSAGE),
  landmark: z.string().max(200),
  pincode: z.string().regex(/^\d{6}$/, "Pincode must be 6 digits"),
  city: z.string().min(1, "City is required").max(100),
  state: z.string().min(1, "State is required").max(100),
  latitude: z.coerce
    .number({ message: "Latitude is required" })
    .min(-90, "Latitude must be between -90 and 90")
    .max(90, "Latitude must be between -90 and 90"),
  longitude: z.coerce
    .number({ message: "Longitude is required" })
    .min(-180, "Longitude must be between -180 and 180")
    .max(180, "Longitude must be between -180 and 180"),
  contactName: z.string().min(1, "Contact name is required").max(200),
  contactMobile: z
    .string()
    .regex(/^\+?[1-9]\d{7,14}$/, "Enter a valid mobile number"),
  isDefault: z.boolean(),
});

export type AddressFormValues = z.input<typeof addressSchema>;
export type AddressPayload = z.output<typeof addressSchema>;

export function AddressForm({
  initial,
  submitLabel,
  error,
  isSubmitting,
  onSubmit,
}: {
  initial?: CustomerAddress;
  submitLabel: string;
  error: string | null;
  isSubmitting: boolean;
  onSubmit: (values: AddressPayload) => void;
}) {
  const form = useForm<AddressFormValues, unknown, AddressPayload>({
    resolver: zodResolver(addressSchema),
    defaultValues: {
      label: initial?.label ?? "Home",
      line1: initial?.line1 ?? "",
      line2: initial?.line2 ?? "",
      landmark: initial?.landmark ?? "",
      pincode: initial?.pincode ?? "",
      city: initial?.city ?? "",
      state: initial?.state ?? "",
      latitude: initial?.latitude ?? 0,
      longitude: initial?.longitude ?? 0,
      contactName: initial?.contactName ?? "",
      contactMobile: initial?.contactMobile ?? "",
      isDefault: initial?.isDefault ?? false,
    },
  });

  const { errors } = form.formState;

  /**
   * Autofills City/State from the geography master once a valid 6-digit
   * pincode is entered (task 369), so the customer doesn't have to type
   * them by hand for a pincode we already know. Both fields stay editable
   * afterwards — an unmapped pincode (404) or a lookup failure just leaves
   * them for manual entry rather than blocking the form.
   */
  async function handlePincodeBlur(e: FocusEvent<HTMLInputElement>) {
    const code = e.target.value;
    if (!/^\d{6}$/.test(code)) return;

    try {
      const location = await apiFetch<PincodeLookup>(`${API_V1}/geography/pincodes/${code}`);
      form.setValue("city", location.cityName, { shouldValidate: true, shouldDirty: true });
      form.setValue("state", location.stateName, { shouldValidate: true, shouldDirty: true });
    } catch {
      // No active pincode matches, or the lookup failed — leave City/State alone.
    }
  }

  return (
    <form
      onSubmit={form.handleSubmit((values) => onSubmit(values))}
      className="flex flex-col gap-7"
      noValidate
    >
      {error ? <Alert>{error}</Alert> : null}

      {/* Section 1 — the address itself. Grouped and sectioned rather than
          stacked as eleven equal full-width boxes: on a wide column that made
          every short field (pincode, city, contact mobile) read as an
          unreasonably long single line. Pairing the naturally-short fields and
          labelling the groups is what fixes the "too long" feel. */}
      <Section title="Address" description="Where the professional should go.">
        <Field label="Label" placeholder="Home, Work…" error={errors.label?.message} {...form.register("label")} />
        <Field label="Address line 1" error={errors.line1?.message} {...form.register("line1")} />
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Field label="Address line 2 (optional)" error={errors.line2?.message} {...form.register("line2")} />
          <Field label="Landmark (optional)" error={errors.landmark?.message} {...form.register("landmark")} />
        </div>
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          <Field
            label="Pincode"
            inputMode="numeric"
            autoComplete="postal-code"
            maxLength={6}
            className="nums"
            hint="City and state fill in automatically."
            error={errors.pincode?.message}
            {...form.register("pincode", { onBlur: handlePincodeBlur })}
          />
          <Field label="City" error={errors.city?.message} {...form.register("city")} />
          <Field label="State" error={errors.state?.message} {...form.register("state")} />
        </div>
      </Section>

      {/* Section 2 — the map pin. Kept together and labelled so the two bare
          numeric fields read as one deliberate "precise location" pair rather
          than two more mystery inputs in the stack. */}
      <Section title="Location pin" description="Helps the professional reach your exact door.">
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Field
            label="Latitude"
            type="number"
            step="any"
            className="nums"
            error={errors.latitude?.message}
            {...form.register("latitude")}
          />
          <Field
            label="Longitude"
            type="number"
            step="any"
            className="nums"
            error={errors.longitude?.message}
            {...form.register("longitude")}
          />
        </div>
      </Section>

      {/* Section 3 — who to call on the day. */}
      <Section title="Contact" description="Who the professional reaches on the day.">
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Field label="Contact name" autoComplete="name" error={errors.contactName?.message} {...form.register("contactName")} />
          <Field
            label="Contact mobile"
            type="tel"
            inputMode="tel"
            autoComplete="tel"
            className="nums"
            error={errors.contactMobile?.message}
            {...form.register("contactMobile")}
          />
        </div>
        <Checkbox label="Use as my default address" {...form.register("isDefault")} />
      </Section>

      {/* Reachable without hunting for it below the fields on a phone (task
          #344 - addresses/new is reached mid-booking via booking/summary's
          "add a new address", so this is a real booking-funnel screen, not only
          account-management furniture; addresses/[id]/edit shares this
          component and gets the same treatment for free rather than forking the
          form in two). */}
      <StickyActionBar>
        <Button type="submit" fullWidth size="lg" disabled={isSubmitting}>
          {isSubmitting ? "Saving…" : submitLabel}
        </Button>
      </StickyActionBar>
    </form>
  );
}

/**
 * A titled group of fields inside the address form. Purely visual structure -
 * a small heading plus a hairline rule - so the eleven-field form reads as
 * three short, scannable sections instead of one long column.
 */
function Section({
  title,
  description,
  children,
}: {
  title: string;
  description: string;
  children: ReactNode;
}) {
  return (
    <fieldset className="flex flex-col gap-4">
      <legend className="mb-1 w-full border-b border-line pb-2">
        <span className="text-sm font-semibold text-fg">{title}</span>
        <span className="ml-2 text-xs text-fg-subtle">{description}</span>
      </legend>
      {children}
    </fieldset>
  );
}

/**
 * Sidebar reassurance shown beside the address form on the add/edit screens -
 * why an address is worth saving and what happens to the contact details.
 * Lives here so both pages render the identical panel rather than each keeping
 * its own copy.
 */
export function AddressHelpCard() {
  return (
    <Card title="Saved for faster booking">
      <ul className="flex flex-col gap-4">
        <HelpItem icon={<PinIcon />} title="One tap at checkout">
          Pick this address instead of retyping it every time you book.
        </HelpItem>
        <HelpItem icon={<StarIcon />} title="Your default">
          Your first saved address becomes your default automatically.
        </HelpItem>
        <HelpItem icon={<ShieldIcon />} title="Shared only when needed">
          Your contact details go only to the professional assigned to your booking.
        </HelpItem>
      </ul>
    </Card>
  );
}

function HelpItem({ icon, title, children }: { icon: ReactNode; title: string; children: ReactNode }) {
  return (
    <li className="flex gap-3">
      <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-brand-50 text-brand-700 dark:bg-brand-500/15 dark:text-brand-300">
        {icon}
      </span>
      <div className="min-w-0">
        <p className="text-sm font-medium text-fg">{title}</p>
        <p className="mt-0.5 text-sm leading-relaxed text-fg-muted">{children}</p>
      </div>
    </li>
  );
}

function PinIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" className="h-4 w-4" aria-hidden>
      <path d="M12 21s7-6.4 7-11a7 7 0 1 0-14 0c0 4.6 7 11 7 11Z" />
      <circle cx="12" cy="10" r="2.5" />
    </svg>
  );
}

function StarIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="currentColor" className="h-4 w-4" aria-hidden>
      <path d="M12 2.5l2.9 6.06 6.6.85-4.85 4.6 1.27 6.57L12 17.4l-5.92 3.18 1.27-6.57-4.85-4.6 6.6-.85L12 2.5Z" />
    </svg>
  );
}

function ShieldIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" className="h-4 w-4" aria-hidden>
      <path d="M12 3l7 3v5.5c0 4.2-3 8-7 9.5-4-1.5-7-5.3-7-9.5V6l7-3Z" />
      <path d="m9 12 2 2 4-4" />
    </svg>
  );
}

/** Empty optional fields go to the API as null rather than "". */
export function toUpsertBody(values: AddressPayload) {
  return {
    ...values,
    line2: values.line2 === "" ? null : values.line2,
    landmark: values.landmark === "" ? null : values.landmark,
  };
}
