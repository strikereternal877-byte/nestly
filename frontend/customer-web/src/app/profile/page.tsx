"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useForm } from "react-hook-form";
import type { UseFormRegisterReturn } from "react-hook-form";
import { z } from "zod";
import { ResendRow, useResendCountdown } from "@/components/auth-ui";
import { OtpInput } from "@/components/OtpInput";
import {
  AccountQuickLinksCard,
  BannerBreadcrumb,
  DetailList,
  DetailRow,
  ScreenSkeleton,
} from "@/components/patterns";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import {
  Alert,
  Button,
  Card,
  Checkbox,
  Field,
  Skeleton,
  useToast,
} from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { todayIsoDate } from "@/lib/date";
import type { CommunicationPreferences, CustomerProfile } from "@/lib/types";

/**
 * Account details, sign-in identifiers and communication preferences
 * (SRS 11.2.3 — GET/PUT /profile, the mobile/email change pairs, and
 * GET/PUT /profile/preferences).
 */
export default function ProfilePage() {
  return (
    <RequireAuth>
      <ProfileScreen />
    </RequireAuth>
  );
}

function ProfileScreen() {
  const profileQuery = useQuery({
    queryKey: ["profile"],
    queryFn: () => apiFetch<CustomerProfile>(`${API_V1}/profile`, { authenticated: true }),
  });

  if (profileQuery.isPending) {
    return (
      <main className="flex w-full flex-col">
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
        <ScreenSkeleton cards={4} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  if (profileQuery.isError) {
    return (
      <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
        <Alert
          tone="error"
          title="Couldn't load your profile"
          action={
            <Button size="sm" variant="secondary" onClick={() => profileQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(profileQuery.error)}
        </Alert>
      </main>
    );
  }

  const profile = profileQuery.data;

  return (
    <main className="flex w-full flex-col animate-rise">
      <PageBanner
        title="Your profile"
        description="View and edit your details and preferences."
        breadcrumb={<BannerBreadcrumb items={[{ label: "Home", href: "/" }, { label: "Your profile" }]} />}
      />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
        <div className="grid grid-cols-1 gap-6 lg:grid-cols-12">
          {/* 8 of 12: everything a customer actually edits. Its own column,
              not the full max-w-7xl width, is what fixes the fields
              themselves reading as unreasonably long single-line boxes. */}
          <div className="flex flex-col gap-6 lg:col-span-8">
            <ProfileDetailsForm profile={profile} />
            <ChangeMobileCard currentMobile={profile.mobile} />
            <ChangeEmailCard currentEmail={profile.email} />
            <PreferencesForm />
          </div>

          {/* 4 of 12: read-only "who you're signed in as" plus the rest of
              the account, so a customer mid-edit can jump to bookings/wallet/
              support without losing their place. Sticky so it stays in view
              beside a form this long. */}
          <div className="flex flex-col gap-6 lg:col-span-4 lg:sticky lg:top-20 lg:self-start">
            <Card
              title="Sign-in details"
              description="Changing either of these needs a verification code."
            >
              <DetailList>
                <DetailRow label="Mobile number" numeric>
                  {profile.mobile}
                </DetailRow>
                <DetailRow label="Email address">
                  {profile.email ?? <span className="font-normal text-fg-subtle">Not set</span>}
                </DetailRow>
              </DetailList>
            </Card>

            <AccountQuickLinksCard currentHref="/profile" />
          </div>
        </div>
      </div>
    </main>
  );
}

/* -------------------------------------------------------------------------- */
/* Personal details                                                           */
/* -------------------------------------------------------------------------- */

/**
 * Mirrors UpdateProfileRequestValidator.
 *
 * `dateOfBirth` previously carried no rule at all while the form still rendered
 * an error slot for it, so the validator's "must be in the past" check lived
 * only on the server: a customer could pick next year, submit, and get back a
 * bare 400. Compared as strings because both sides are `YYYY-MM-DD`, which
 * sorts lexicographically — no Date parsing, so no timezone in the path.
 */
const profileSchema = z.object({
  name: z
    .string()
    .trim()
    .min(1, "Name is required")
    .max(200, "Name must be 200 characters or fewer"),
  dateOfBirth: z.union([
    z.string().refine((value) => value < todayIsoDate(), "Date of birth must be in the past"),
    z.literal(""),
  ]),
  city: z.string().max(200, "City must be 200 characters or fewer"),
  state: z.string().max(200, "State must be 200 characters or fewer"),
  pincode: z.union([z.string().regex(/^\d{6}$/, "Pincode must be 6 digits"), z.literal("")]),
  country: z.string().max(200, "Country must be 200 characters or fewer"),
});

type ProfileFormValues = z.infer<typeof profileSchema>;

/** The API sends `DateTime?` as `1990-05-15T00:00:00`; the calendar day is the
 *  leading 10 characters. Sliced as text rather than parsed into a `Date` —
 *  parse-and-reformat is what turns a birthday into the day before it. */
function dateInputValue(dateOfBirth: string | null): string {
  return dateOfBirth ? dateOfBirth.slice(0, 10) : "";
}

function toFormValues(profile: CustomerProfile): ProfileFormValues {
  return {
    name: profile.name,
    dateOfBirth: dateInputValue(profile.dateOfBirth),
    city: profile.city ?? "",
    state: profile.state ?? "",
    pincode: profile.pincode ?? "",
    country: profile.country ?? "",
  };
}

function ProfileDetailsForm({ profile }: { profile: CustomerProfile }) {
  const queryClient = useQueryClient();
  const toast = useToast();
  const [error, setError] = useState<string | null>(null);

  const form = useForm<ProfileFormValues>({
    resolver: zodResolver(profileSchema),
    defaultValues: toFormValues(profile),
  });

  const mutation = useMutation({
    mutationFn: (values: ProfileFormValues) =>
      apiFetch<CustomerProfile>(`${API_V1}/profile`, {
        method: "PUT",
        authenticated: true,
        body: JSON.stringify({
          name: values.name.trim(),
          // The optional columns are nullable server-side; an empty string
          // would be stored as a blank value rather than "not provided".
          dateOfBirth: values.dateOfBirth === "" ? null : values.dateOfBirth,
          city: values.city === "" ? null : values.city,
          state: values.state === "" ? null : values.state,
          pincode: values.pincode === "" ? null : values.pincode,
          country: values.country === "" ? null : values.country,
        }),
      }),
    onSuccess: (updated) => {
      queryClient.setQueryData(["profile"], updated);
      setError(null);
      // Reset from the server's own response: it clears the dirty state and
      // shows what was actually stored rather than what was typed.
      form.reset(toFormValues(updated));
      toast("success", "Profile saved.");
    },
    onError: (err) => setError(describeError(err)),
  });

  return (
    <Card
      title="Personal details"
      description="Everything except your name is optional, and only used to speed up booking."
    >
      <form
        onSubmit={form.handleSubmit((values) => {
          if (mutation.isPending) return;
          mutation.mutate(values);
        })}
        className="flex flex-col gap-4"
        noValidate
      >
        {error ? (
          <Alert tone="error" title="Couldn't save your profile">
            {error}
          </Alert>
        ) : null}

        <Field
          label="Full name"
          autoComplete="name"
          required
          error={form.formState.errors.name?.message}
          {...form.register("name")}
        />
        <Field
          label="Date of birth"
          type="date"
          // The same bound the schema enforces, so the native picker cannot
          // offer a day the form would then reject.
          max={todayIsoDate()}
          error={form.formState.errors.dateOfBirth?.message}
          {...form.register("dateOfBirth")}
        />
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Field
            label="City"
            autoComplete="address-level2"
            error={form.formState.errors.city?.message}
            {...form.register("city")}
          />
          <Field
            label="State"
            autoComplete="address-level1"
            error={form.formState.errors.state?.message}
            {...form.register("state")}
          />
        </div>
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <Field
            label="Pincode"
            inputMode="numeric"
            autoComplete="postal-code"
            maxLength={6}
            className="nums"
            error={form.formState.errors.pincode?.message}
            {...form.register("pincode")}
          />
          <Field
            label="Country"
            autoComplete="country-name"
            error={form.formState.errors.country?.message}
            {...form.register("country")}
          />
        </div>

        <Button type="submit" className="self-start" loading={mutation.isPending}>
          Save changes
        </Button>
      </form>
    </Card>
  );
}

/* -------------------------------------------------------------------------- */
/* Mobile / email change                                                      */
/* -------------------------------------------------------------------------- */

const otpCodeSchema = z.string().regex(/^\d{6}$/, "Enter the 6-digit code");

const mobileChangeSchema = z.object({
  identifier: z.string().regex(/^\+?[1-9]\d{7,14}$/, "Enter a valid mobile number"),
  otpCode: otpCodeSchema,
});

const emailChangeSchema = z.object({
  identifier: z.email("Enter a valid email address"),
  otpCode: otpCodeSchema,
});

function ChangeMobileCard({ currentMobile }: { currentMobile: string }) {
  return (
    <ChangeIdentifierCard
      title="Change mobile number"
      description={`Currently ${currentMobile}.`}
      label="New mobile number"
      idPrefix="mobile"
      schema={mobileChangeSchema}
      requestPath={`${API_V1}/profile/mobile/otp`}
      confirmPath={`${API_V1}/profile/mobile`}
      requestKey="newMobile"
      confirmKey="newMobile"
      successMessage="Mobile number updated."
      resetLabel="Use a different number"
      inputProps={{
        type: "tel",
        inputMode: "tel",
        autoComplete: "tel",
        placeholder: "+919876543210",
        className: "nums",
      }}
    />
  );
}

function ChangeEmailCard({ currentEmail }: { currentEmail: string | null }) {
  return (
    <ChangeIdentifierCard
      title="Change email address"
      description={currentEmail ? `Currently ${currentEmail}.` : "No email address set."}
      label="New email address"
      idPrefix="email"
      schema={emailChangeSchema}
      requestPath={`${API_V1}/profile/email/otp`}
      confirmPath={`${API_V1}/profile/email`}
      requestKey="newEmail"
      confirmKey="newEmail"
      successMessage="Email address updated."
      resetLabel="Use a different address"
      inputProps={{ type: "email", autoComplete: "email" }}
    />
  );
}

interface ChangeIdentifierValues {
  identifier: string;
  otpCode: string;
}

/**
 * The two-step "prove you own the new one" flow shared by the mobile and email
 * change cards. One component rather than two near-identical ones because the
 * pair only differ in their endpoints, their validation rule and their wording
 * — and the parts that matter (the rate-limit guard, freezing the identifier
 * once a code is out) are exactly the parts that were drifting between them.
 */
function ChangeIdentifierCard({
  title,
  description,
  label,
  idPrefix,
  schema,
  requestPath,
  confirmPath,
  requestKey,
  confirmKey,
  successMessage,
  resetLabel,
  inputProps,
}: {
  title: string;
  description: string;
  label: string;
  /** Namespaces the control ids — both cards live on the same page, and two
   *  controls sharing an id leaves one of them with no working label. */
  idPrefix: string;
  /** Both the input and the output are the plain value shape — the schema
   *  validates, it does not transform, so `zodResolver` needs both parameters
   *  pinned or it widens the form's field type to `FieldValues`. */
  schema: z.ZodType<ChangeIdentifierValues, ChangeIdentifierValues>;
  requestPath: string;
  confirmPath: string;
  requestKey: string;
  confirmKey: string;
  successMessage: string;
  resetLabel: string;
  inputProps: Omit<React.InputHTMLAttributes<HTMLInputElement>, "id" | keyof UseFormRegisterReturn>;
}) {
  const queryClient = useQueryClient();
  const toast = useToast();
  const [error, setError] = useState<string | null>(null);
  const [sentTo, setSentTo] = useState<string | null>(null);
  const { remaining, start, canResend } = useResendCountdown();

  const form = useForm<ChangeIdentifierValues>({
    resolver: zodResolver(schema),
    defaultValues: { identifier: "", otpCode: "" },
  });

  /**
   * Both OTP endpoints sit behind the "otp" rate limiter, so an unguarded
   * button doesn't merely send two codes — it spends the customer's allowance
   * and can lock them out of the change they are trying to make. Driving it
   * through a mutation gives the single in-flight state the old bare async
   * handler had no way to express.
   */
  const requestOtp = useMutation({
    mutationFn: async (identifier: string) => {
      await apiFetch(requestPath, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ [requestKey]: identifier }),
      });
      return identifier;
    },
    onSuccess: (identifier) => {
      setError(null);
      setSentTo(identifier);
      start();
    },
    onError: (err) => setError(describeError(err)),
  });

  const confirm = useMutation({
    mutationFn: (values: ChangeIdentifierValues) =>
      apiFetch<CustomerProfile>(confirmPath, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ [confirmKey]: values.identifier, otpCode: values.otpCode }),
      }),
    onSuccess: (updated) => {
      queryClient.setQueryData(["profile"], updated);
      form.reset({ identifier: "", otpCode: "" });
      setError(null);
      setSentTo(null);
      toast("success", successMessage);
    },
    onError: (err) => setError(describeError(err)),
  });

  /** Validates only the identifier — the code doesn't exist yet at this step. */
  const sendCode = () => {
    // `loading` already disables the button; this drops a second click landing
    // in the same tick, before that disable has rendered.
    if (requestOtp.isPending) return;
    setError(null);
    const identifier = form.getValues("identifier");
    const parsed = schema.safeParse({ identifier, otpCode: "000000" });
    if (!parsed.success) {
      const issue = parsed.error.issues.find((candidate) => candidate.path[0] === "identifier");
      if (issue) {
        form.setError("identifier", { message: issue.message });
        return;
      }
    }
    requestOtp.mutate(identifier);
  };

  return (
    <Card title={title} description={description}>
      <form
        onSubmit={form.handleSubmit((values) => {
          if (confirm.isPending) return;
          confirm.mutate(values);
        })}
        className="flex flex-col gap-4"
        noValidate
      >
        {error ? (
          <Alert tone="error" title="Couldn't complete this change">
            {error}
          </Alert>
        ) : null}

        <Field
          {...inputProps}
          label={label}
          id={`${idPrefix}-identifier`}
          // Frozen once a code is out: editing it while the code sitting in the
          // customer's inbox belongs to the previous value would submit a
          // mismatched pair, which reads to them as "wrong code".
          readOnly={sentTo !== null}
          error={form.formState.errors.identifier?.message}
          {...form.register("identifier")}
        />

        {sentTo ? (
          <>
            <p role="status" className="text-sm leading-relaxed text-fg-muted">
              We sent a code to <span className="font-medium text-fg">{sentTo}</span>. Enter it below
              to confirm the change.
            </p>

            <OtpInput
              label="6-digit code"
              error={form.formState.errors.otpCode?.message}
              {...form.register("otpCode")}
            />

            <ResendRow
              remaining={remaining}
              canResend={canResend}
              pending={requestOtp.isPending}
              onResend={() => requestOtp.mutate(sentTo)}
            />

            <div className="flex flex-wrap gap-2">
              <Button type="submit" loading={confirm.isPending}>
                Confirm change
              </Button>
              <Button
                type="button"
                variant="ghost"
                disabled={confirm.isPending}
                onClick={() => {
                  setSentTo(null);
                  setError(null);
                  form.setValue("otpCode", "");
                }}
              >
                {resetLabel}
              </Button>
            </div>
          </>
        ) : (
          <Button
            type="button"
            variant="secondary"
            className="self-start"
            loading={requestOtp.isPending}
            onClick={sendCode}
          >
            Send verification code
          </Button>
        )}
      </form>
    </Card>
  );
}

/* -------------------------------------------------------------------------- */
/* Communication preferences                                                  */
/* -------------------------------------------------------------------------- */

/**
 * Grouped rather than a flat list of seven: "Booking and account updates by
 * SMS" and "Offers and promotions by SMS" only differ in their prefix, and the
 * grouping is what makes the transactional/promotional split — the one that
 * actually matters to a customer — legible at a glance.
 */
const PREFERENCE_GROUPS = [
  {
    heading: "Booking and account updates",
    hint: "Confirmations, professional assignment, reschedules and refunds.",
    fields: [
      { key: "transactionalSms", label: "By SMS" },
      { key: "transactionalEmail", label: "By email" },
      { key: "transactionalWhatsApp", label: "On WhatsApp" },
    ],
  },
  {
    heading: "Offers and promotions",
    hint: "Discounts, new services in your area and seasonal campaigns.",
    fields: [
      { key: "promotionalSms", label: "By SMS" },
      { key: "promotionalEmail", label: "By email" },
      { key: "promotionalWhatsApp", label: "On WhatsApp" },
    ],
  },
  {
    heading: "In-app",
    hint: null,
    fields: [{ key: "push", label: "Push notifications" }],
  },
] as const;

type PreferenceKey = (typeof PREFERENCE_GROUPS)[number]["fields"][number]["key"];
type PreferenceState = Record<PreferenceKey, boolean>;

const PREFERENCE_KEYS: PreferenceKey[] = PREFERENCE_GROUPS.flatMap((group) =>
  group.fields.map((field) => field.key),
);

function PreferencesForm() {
  const queryClient = useQueryClient();
  const toast = useToast();
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<PreferenceState | null>(null);

  const query = useQuery({
    queryKey: ["preferences"],
    queryFn: () =>
      apiFetch<CommunicationPreferences>(`${API_V1}/profile/preferences`, { authenticated: true }),
  });

  // The checkboxes are edited locally and saved as one batch, so the server
  // response seeds a draft rather than being rendered from the cache
  // directly. Reseeded on every change of `data` (react-query keeps the same
  // reference across structurally-equal refetches, so this doesn't clobber
  // in-progress edits on a no-op background refetch).
  const { data } = query;
  const [prevData, setPrevData] = useState(data);
  if (data !== prevData) {
    setPrevData(data);
    if (data) setDraft(Object.fromEntries(PREFERENCE_KEYS.map((key) => [key, data[key]])) as PreferenceState);
  }

  const mutation = useMutation({
    mutationFn: (values: PreferenceState) =>
      apiFetch<CommunicationPreferences>(`${API_V1}/profile/preferences`, {
        method: "PUT",
        authenticated: true,
        body: JSON.stringify(values),
      }),
    onSuccess: (updated) => {
      queryClient.setQueryData(["preferences"], updated);
      setError(null);
      toast("success", "Preferences saved.");
    },
    onError: (err) => setError(describeError(err)),
  });

  // The error branch has to be tested before the draft is. On a failed request
  // `isPending` is false but the draft is still null, so checking the draft
  // first left this card stuck on "Loading preferences…" forever, with the
  // error state unreachable and no way to retry.
  if (query.isError) {
    return (
      <Card title="Communication preferences">
        <Alert
          tone="error"
          title="Couldn't load your preferences"
          action={
            <Button size="sm" variant="secondary" onClick={() => query.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(query.error)}
        </Alert>
      </Card>
    );
  }

  if (query.isPending || !draft) {
    return (
      <Card title="Communication preferences">
        <div className="flex flex-col gap-6" aria-hidden>
          {PREFERENCE_GROUPS.map((group) => (
            <div key={group.heading} className="flex flex-col gap-2.5">
              <Skeleton className="h-4 w-48" />
              {group.fields.map((field) => (
                <Skeleton key={field.key} className="h-4 w-40" />
              ))}
            </div>
          ))}
          <Skeleton className="h-10 w-40 rounded-lg" />
        </div>
      </Card>
    );
  }

  return (
    <Card
      title="Communication preferences"
      description="Security codes are always sent, and are not affected by these settings."
    >
      <form
        onSubmit={(event) => {
          event.preventDefault();
          if (mutation.isPending) return;
          mutation.mutate(draft);
        }}
        className="flex flex-col gap-6"
      >
        {error ? (
          <Alert tone="error" title="Couldn't save your preferences">
            {error}
          </Alert>
        ) : null}

        {PREFERENCE_GROUPS.map((group) => (
          // A real fieldset/legend: three of these checkboxes are labelled
          // "By SMS" and three "By email", so read out of context — which is
          // exactly how a screen reader walks a form — they are otherwise
          // indistinguishable from one another.
          <fieldset key={group.heading} className="flex flex-col gap-2.5">
            <legend className="text-sm font-medium text-fg">{group.heading}</legend>
            {group.hint ? <p className="mb-1 text-xs text-fg-muted">{group.hint}</p> : null}
            {group.fields.map((field) => (
              <Checkbox
                key={field.key}
                name={field.key}
                label={field.label}
                checked={draft[field.key]}
                disabled={mutation.isPending}
                onChange={(event) => setDraft({ ...draft, [field.key]: event.target.checked })}
              />
            ))}
          </fieldset>
        ))}

        <Button type="submit" className="self-start" loading={mutation.isPending}>
          Save preferences
        </Button>
      </form>
    </Card>
  );
}
