"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import {
  AuthShell,
  ResendRow,
  Segmented,
  useResendCountdown,
} from "@/components/auth-ui";
import { OtpInput } from "@/components/OtpInput";
import { Alert, Button, Field } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { storeSession } from "@/lib/auth";
import { RETURN_TO_PARAM, resolvePostLoginPath } from "@/lib/return-to";
import type { LoginResponse } from "@/lib/types";
import {
  PROVIDER_WEB_URL,
  loginProviderWithPassword,
  redirectWithSession,
  requestProviderLoginOtp,
  verifyProviderLoginOtp,
} from "@/lib/unified-login-api";

// Mirrors the server-side FluentValidation rules (LoginValidators.cs) so the
// common mistakes are caught before a request is spent. The server remains
// the authority — this is convenience, not trust.
const mobileSchema = z
  .string()
  .regex(/^\+?[1-9]\d{7,14}$/, "Enter a valid mobile number");

const otpRequestSchema = z.object({ mobile: mobileSchema });
const otpVerifySchema = z.object({
  mobile: mobileSchema,
  otpCode: z.string().regex(/^\d{6}$/, "Enter the 6-digit code"),
});
const passwordSchema = z.object({
  email: z.email("Enter a valid email address"),
  password: z.string().min(1, "Password is required"),
});
const providerMobileSchema = z.object({ mobile: mobileSchema });
// ProviderOtpService.GenerateAsync (backend/shared) always generates a
// 6-digit code, same as the customer flow - mirror that here rather than the
// looser 4-8 range the unified login previously accepted.
const providerOtpSchema = z.object({
  mobile: mobileSchema,
  otpCode: z.string().regex(/^\d{6}$/, "Enter the 6-digit code"),
});

type Mode = "otp" | "password";
// Staff (admin) sign-in deliberately has no entry point here - task 206's
// unified switcher used to offer Customer / Admin / Provider on this public
// consumer domain, exposing the internal admin authentication surface on
// the same origin a shopper lands on. admin-web keeps its own `/login` at
// its own (internal) origin; this app only ever authenticates customers and
// providers.
type AccountType = "customer" | "provider";

const ACCOUNT_TYPES = [
  { value: "customer" as const, label: "Customer" },
  { value: "provider" as const, label: "Provider" },
];

const SIGN_IN_MODES = [
  { value: "otp" as const, label: "Mobile OTP" },
  { value: "password" as const, label: "Email & password" },
];

/**
 * Mobile OTP sign-in is hidden for now (email + password is the primary
 * path) - flip this back to true to bring the toggle back. Nothing else
 * needs to change: OtpLogin/ProviderOtpLoginUnified and their backend
 * endpoints are untouched.
 */
const SHOW_MOBILE_OTP_LOGIN = false;

/**
 * Sign-in entry point for customer-web (task 206), also reachable by a
 * provider who lands here instead of provider-web's own origin. There is no
 * shared parent domain across the three frontends yet (docs/DEVOPS.md's
 * hosting/domain decisions are still open), so a provider sign-in still
 * authenticates against provider-api directly from here, then hands the
 * browser off to provider-web's own origin with the session in the URL
 * fragment (see lib/unified-login-api.ts) rather than a subdomain-gateway/
 * shared-cookie approach, which real infra doesn't exist to support yet.
 * provider-api keeps issuing its own independently-audienced token exactly
 * as before - only the routing to reach it is shared.
 *
 * Deliberately does NOT offer admin sign-in: staff authentication is not
 * exposed on this public consumer-facing domain (see the Account type
 * fix note on `AccountType` above). admin-web keeps its own `/login` at its
 * own origin.
 *
 * provider-web's own `/login` page is intentionally left in place (not
 * removed) so a bookmarked/direct visit to that app's origin still works.
 */
export default function LoginPage() {
  // Suspense for useSearchParams below (see booking/summary/page.tsx for the
  // same pattern): reading the `next` destination opts this tree out of
  // static rendering, which the App Router requires a boundary around.
  return (
    <Suspense fallback={<AuthShell title="Welcome back" subtitle="One sign-in for customers, admins and providers."><div /></AuthShell>}>
      <LoginScreen />
    </Suspense>
  );
}

/**
 * Where to go once signed in: the screen the customer was sent here from, or
 * their profile if they came to `/login` directly. See lib/return-to.ts for
 * why the value is validated rather than followed as given.
 */
function usePostLoginPath(): string {
  const returnTo = useSearchParams().get(RETURN_TO_PARAM);
  return resolvePostLoginPath(returnTo);
}

function LoginScreen() {
  const [accountType, setAccountType] = useState<AccountType>("customer");
  const [mode, setMode] = useState<Mode>(SHOW_MOBILE_OTP_LOGIN ? "otp" : "password");

  return (
    <AuthShell
      title="Welcome back"
      subtitle="One sign-in for customers, admins and providers."
      footer={
        accountType === "customer" ? (
          <>
            New to Glavyx?{" "}
            <Link
              href="/register"
              className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
            >
              Create an account
            </Link>
          </>
        ) : null
      }
    >
      <div className="flex flex-col gap-5">
        <Segmented
          name="account-type"
          label="Account type"
          options={ACCOUNT_TYPES}
          value={accountType}
          onChange={setAccountType}
        />

        {accountType === "customer" ? (
          <>
            {SHOW_MOBILE_OTP_LOGIN ? (
              <Segmented
                name="sign-in-mode"
                label="Sign-in method"
                options={SIGN_IN_MODES}
                value={mode}
                onChange={setMode}
              />
            ) : null}
            {mode === "otp" ? <OtpLogin /> : <PasswordLogin />}
          </>
        ) : (
          <ProviderLoginUnified />
        )}
      </div>
    </AuthShell>
  );
}

function OtpLogin() {
  const router = useRouter();
  const postLoginPath = usePostLoginPath();
  const [step, setStep] = useState<"request" | "verify">("request");
  const [mobile, setMobile] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [resending, setResending] = useState(false);
  const { remaining, start, canResend } = useResendCountdown();

  const requestForm = useForm<z.infer<typeof otpRequestSchema>>({
    resolver: zodResolver(otpRequestSchema),
    defaultValues: { mobile: "" },
  });

  const verifyForm = useForm<z.infer<typeof otpVerifySchema>>({
    resolver: zodResolver(otpVerifySchema),
    defaultValues: { mobile: "", otpCode: "" },
  });

  const sendCode = async (value: string) => {
    await apiFetch(`${API_V1}/auth/login/otp`, {
      method: "POST",
      body: JSON.stringify({ mobile: value }),
    });
    start();
  };

  const onRequest = requestForm.handleSubmit(async ({ mobile: value }) => {
    setError(null);
    try {
      await sendCode(value);
      setMobile(value);
      verifyForm.setValue("mobile", value);
      setNotice(`We sent a 6-digit code to ${value}.`);
      setStep("verify");
    } catch (err) {
      setError(describeError(err));
    }
  });

  const onResend = async () => {
    setError(null);
    setResending(true);
    try {
      await sendCode(mobile);
      setNotice(`We sent a new code to ${mobile}.`);
    } catch (err) {
      setError(describeError(err));
    } finally {
      setResending(false);
    }
  };

  const onVerify = verifyForm.handleSubmit(async (values) => {
    setError(null);
    try {
      const session = await apiFetch<LoginResponse>(`${API_V1}/auth/login/otp/verify`, {
        method: "POST",
        body: JSON.stringify(values),
      });
      storeSession(session);
      router.push(postLoginPath);
    } catch (err) {
      setError(describeError(err));
    }
  });

  if (step === "request") {
    return (
      <form method="post" onSubmit={onRequest} className="flex flex-col gap-4" noValidate>
        {/* method="post" is defence in depth, not routing: react-hook-form's
            handleSubmit preventDefaults every real submit, so this attribute never
            takes effect once the page is interactive. It matters for a submit that
            lands *before* hydration (slow JS, a failed chunk, an extension), which
            falls back to the browser's native behaviour - and a form with no method
            defaults to GET, which would put the password or OTP into the URL, the
            browser history, the server access log and any outbound Referer header.
            POST keeps them in a request body. */}
        {error ? <Alert>{error}</Alert> : null}
        <Field
          label="Mobile number"
          type="tel"
          autoComplete="tel"
          placeholder="+919876543210"
          hint="We'll text you a 6-digit code."
          error={requestForm.formState.errors.mobile?.message}
          {...requestForm.register("mobile")}
        />
        <Button type="submit" size="lg" fullWidth loading={requestForm.formState.isSubmitting}>
          Send code
        </Button>
      </form>
    );
  }

  return (
    <form method="post" onSubmit={onVerify} className="flex flex-col gap-4" noValidate>
      {error ? <Alert>{error}</Alert> : null}
      {notice ? <Alert tone="info">{notice}</Alert> : null}

      <OtpInput
        error={verifyForm.formState.errors.otpCode?.message}
        {...verifyForm.register("otpCode")}
      />
      <Button type="submit" size="lg" fullWidth loading={verifyForm.formState.isSubmitting}>
        Verify and sign in
      </Button>

      <ResendRow
        remaining={remaining}
        canResend={canResend}
        onResend={onResend}
        pending={resending}
      />

      <Button
        type="button"
        variant="ghost"
        onClick={() => {
          setStep("request");
          setNotice(null);
          setError(null);
        }}
      >
        Use a different number ({mobile})
      </Button>
    </form>
  );
}

function PasswordLogin() {
  const router = useRouter();
  const postLoginPath = usePostLoginPath();
  const [error, setError] = useState<string | null>(null);

  const form = useForm<z.infer<typeof passwordSchema>>({
    resolver: zodResolver(passwordSchema),
    defaultValues: { email: "", password: "" },
  });

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    try {
      const session = await apiFetch<LoginResponse>(`${API_V1}/auth/login/password`, {
        method: "POST",
        body: JSON.stringify(values),
      });
      storeSession(session);
      // /install-app shows the "add to home screen" steps on a mobile
      // browser that hasn't seen them before, then forwards on to
      // postLoginPath itself - see that page for the skip conditions.
      router.push(`/install-app?next=${encodeURIComponent(postLoginPath)}`);
    } catch (err) {
      setError(describeError(err));
    }
  });

  return (
    <form method="post" onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
      {error ? <Alert>{error}</Alert> : null}
      <Field
        label="Email"
        type="email"
        autoComplete="email"
        error={form.formState.errors.email?.message}
        {...form.register("email")}
      />
      <Field
        label="Password"
        type="password"
        autoComplete="current-password"
        error={form.formState.errors.password?.message}
        {...form.register("password")}
      />
      <Button type="submit" size="lg" fullWidth loading={form.formState.isSubmitting}>
        Sign in
      </Button>
      <Link
        href="/forgot-password"
        className="text-center text-sm text-fg-muted underline-offset-4 hover:text-fg hover:underline"
      >
        Forgot your password?
      </Link>
    </form>
  );
}

/**
 * Provider sign-in from the unified entry point - calls provider-api
 * directly, then hands off to provider-web's own origin. Task 372 added a
 * sign-in-mode toggle here too, mirroring the Customer branch's own
 * OTP/password toggle above.
 */
function ProviderLoginUnified() {
  const [mode, setMode] = useState<Mode>(SHOW_MOBILE_OTP_LOGIN ? "otp" : "password");

  return (
    <>
      {SHOW_MOBILE_OTP_LOGIN ? (
        <Segmented
          name="provider-sign-in-mode"
          label="Sign-in method"
          options={SIGN_IN_MODES}
          value={mode}
          onChange={setMode}
        />
      ) : null}
      {mode === "otp" ? <ProviderOtpLoginUnified /> : <ProviderPasswordLoginUnified />}
    </>
  );
}

function ProviderPasswordLoginUnified() {
  const [error, setError] = useState<string | null>(null);

  const form = useForm<z.infer<typeof passwordSchema>>({
    resolver: zodResolver(passwordSchema),
    defaultValues: { email: "", password: "" },
  });

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    try {
      const session = await loginProviderWithPassword(values.email, values.password);
      redirectWithSession(PROVIDER_WEB_URL, "/jobs", session);
    } catch (err) {
      setError(describeError(err));
    }
  });

  return (
    <form method="post" onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
      {error ? <Alert>{error}</Alert> : null}
      <Field
        label="Email"
        type="email"
        autoComplete="email"
        error={form.formState.errors.email?.message}
        {...form.register("email")}
      />
      <Field
        label="Password"
        type="password"
        autoComplete="current-password"
        error={form.formState.errors.password?.message}
        {...form.register("password")}
      />
      <Button type="submit" size="lg" fullWidth loading={form.formState.isSubmitting}>
        Sign in
      </Button>
      <p className="text-center text-xs text-fg-subtle">
        You&apos;ll be taken to the provider portal on its own address.
      </p>
    </form>
  );
}

function ProviderOtpLoginUnified() {
  const [step, setStep] = useState<"request" | "verify">("request");
  const [mobile, setMobile] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [resending, setResending] = useState(false);
  const { remaining, start, canResend } = useResendCountdown();

  const requestForm = useForm<z.infer<typeof providerMobileSchema>>({
    resolver: zodResolver(providerMobileSchema),
    defaultValues: { mobile: "" },
  });

  const verifyForm = useForm<z.infer<typeof providerOtpSchema>>({
    resolver: zodResolver(providerOtpSchema),
    defaultValues: { mobile: "", otpCode: "" },
  });

  const onRequest = requestForm.handleSubmit(async ({ mobile: value }) => {
    setError(null);
    try {
      await requestProviderLoginOtp(value);
      start();
      setMobile(value);
      verifyForm.setValue("mobile", value);
      setNotice(`We sent a verification code to ${value}.`);
      setStep("verify");
    } catch (err) {
      setError(describeError(err));
    }
  });

  const onResend = async () => {
    setError(null);
    setResending(true);
    try {
      await requestProviderLoginOtp(mobile);
      start();
      setNotice(`We sent a new code to ${mobile}.`);
    } catch (err) {
      setError(describeError(err));
    } finally {
      setResending(false);
    }
  };

  const onVerify = verifyForm.handleSubmit(async (values) => {
    setError(null);
    try {
      const session = await verifyProviderLoginOtp(values.mobile, values.otpCode);
      redirectWithSession(PROVIDER_WEB_URL, "/jobs", session);
    } catch (err) {
      setError(describeError(err));
    }
  });

  if (step === "request") {
    return (
      <form method="post" onSubmit={onRequest} className="flex flex-col gap-4" noValidate>
        {error ? <Alert>{error}</Alert> : null}
        <Field
          label="Mobile number"
          type="tel"
          autoComplete="tel"
          placeholder="e.g. 9876543210"
          hint="Use the number registered with your provider account."
          error={requestForm.formState.errors.mobile?.message}
          {...requestForm.register("mobile")}
        />
        <Button type="submit" size="lg" fullWidth loading={requestForm.formState.isSubmitting}>
          Send verification code
        </Button>
      </form>
    );
  }

  return (
    <form method="post" onSubmit={onVerify} className="flex flex-col gap-4" noValidate>
      {error ? <Alert>{error}</Alert> : null}
      {notice ? <Alert tone="info">{notice}</Alert> : null}

      {/* Same 6-digit code as the customer flow - see providerOtpSchema. */}
      <OtpInput
        error={verifyForm.formState.errors.otpCode?.message}
        {...verifyForm.register("otpCode")}
      />
      <Button type="submit" size="lg" fullWidth loading={verifyForm.formState.isSubmitting}>
        Sign in
      </Button>

      <ResendRow
        remaining={remaining}
        canResend={canResend}
        onResend={onResend}
        pending={resending}
      />

      <Button
        type="button"
        variant="ghost"
        onClick={() => {
          setStep("request");
          setNotice(null);
          setError(null);
        }}
      >
        Use a different number ({mobile})
      </Button>
    </form>
  );
}
