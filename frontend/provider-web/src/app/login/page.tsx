"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { AuthShell, ResendRow, Segmented, useResendCountdown } from "@/components/auth-ui";
import { OtpInput } from "@/components/OtpInput";
import { Alert, Button, Field, IconButton } from "@/components/ui";
import { describeError, describeLoginError } from "@/lib/api";
import { loginWithPassword, requestLoginOtp, verifyLoginOtp } from "@/lib/auth-api";
import { isAuthenticated, storeSession, subscribeToAuthChanges } from "@/lib/auth";
import type { ProviderLoginResponse } from "@/lib/types";

// Dev-only test-auth backdoor (docs/DEVOPS.md "Dev-only provider test
// login"). NEXT_PUBLIC_ENABLE_DEV_AUTH is unset by default in every
// committed env file, so this button and the /api/dev-login route it calls
// are both dark in a normal checkout - it only appears when a developer
// opts in locally. The shared secret itself never reaches this bundle: it
// stays server-side in /api/dev-login (see that route for why).
const DEV_AUTH_ENABLED = process.env.NEXT_PUBLIC_ENABLE_DEV_AUTH === "true";

// Basic shape validation before spending a request; the server remains the
// authority on what counts as a valid mobile number.
const mobileSchema = z.object({
  mobile: z
    .string()
    .min(7, "Enter a valid mobile number")
    .max(15, "Enter a valid mobile number")
    .regex(/^[0-9+]+$/, "Digits only (a leading + is fine)"),
});
type MobileFormValues = z.infer<typeof mobileSchema>;

const otpSchema = z.object({
  otpCode: z
    .string()
    .min(4, "Enter the code you received")
    .max(8, "Enter the code you received")
    .regex(/^[0-9]+$/, "The code is numeric"),
});
type OtpFormValues = z.infer<typeof otpSchema>;

const passwordSchema = z.object({
  email: z.email("Enter a valid email address"),
  password: z.string().min(1, "Password is required"),
});
type PasswordFormValues = z.infer<typeof passwordSchema>;

type SignInMode = "otp" | "password";

const SIGN_IN_MODES = [
  { value: "otp" as const, label: "Mobile OTP" },
  { value: "password" as const, label: "Email & password" },
];

/**
 * Mobile OTP sign-in is hidden for now (email + password is the primary
 * path) - flip this back to true to bring the toggle back. Nothing else
 * needs to change: OtpLogin and its backend endpoints are untouched.
 */
const SHOW_MOBILE_OTP_LOGIN = false;

/**
 * Provider sign-in. Task 372 added an email+password mode alongside the
 * original OTP-only flow (docs/PROVIDER.md), mirroring customer-web's own
 * OTP/password toggle exactly.
 *
 * This page is deliberately retained (task #206) even though customer-web's
 * unified /login can also authenticate a provider: a bookmarked or directly
 * typed provider-web origin must still be able to sign in. The direct
 * provider-api calls below are exactly what #206 specified, unchanged.
 */
export default function ProviderLoginPage() {
  const router = useRouter();
  const [mode, setMode] = useState<SignInMode>(SHOW_MOBILE_OTP_LOGIN ? "otp" : "password");
  const [infoMessage, setInfoMessage] = useState<string | null>(null);

  // Already signed in (e.g. back-button to /login with a live session) -
  // send straight to Today instead of showing the form again.
  useEffect(() => {
    const sync = () => {
      if (isAuthenticated()) router.replace("/today");
    };
    sync();
    return subscribeToAuthChanges(sync);
  }, [router]);

  // Read the query string directly (rather than next/navigation's
  // useSearchParams) so this page can stay statically prerendered - a
  // useSearchParams call forces the whole page out of static rendering
  // unless wrapped in its own Suspense boundary, which is unnecessary
  // machinery for a couple of one-off banners.
  useEffect(() => {
    if (typeof window === "undefined") return;
    const params = new URLSearchParams(window.location.search);
    if (params.get("registered") === "1") {
      setInfoMessage("Registration submitted. Sign in below to continue.");
    } else if (params.get("reason") === "expired") {
      // Set by RequireProviderAuth when it redirects here after a live
      // session lapsed (as opposed to nobody having been signed in) - see
      // that component. Without this, the redirect landed on a bare sign-in
      // form and any credentials error from a *subsequent* failed attempt
      // could read as if the still-valid password had just stopped working.
      setInfoMessage("Your session expired. Please sign in again.");
    }
  }, []);

  const [devSigningIn, setDevSigningIn] = useState(false);
  const [devError, setDevError] = useState<string | null>(null);
  const devSignIn = async () => {
    setDevError(null);
    setDevSigningIn(true);
    try {
      const response = await fetch("/api/dev-login", { method: "POST" });
      if (!response.ok) {
        throw new Error("Dev sign-in failed. Is provider-api running with DevAuth configured?");
      }
      const session = (await response.json()) as ProviderLoginResponse;
      storeSession(session);
      router.push("/today");
    } catch (err) {
      setDevError(describeError(err));
    } finally {
      setDevSigningIn(false);
    }
  };

  return (
    <AuthShell
      title="Provider sign in"
      subtitle="Sign in to manage your jobs and profile."
      footer={
        <>
          New provider?{" "}
          <Link
            href="/register"
            className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
          >
            Register here
          </Link>
        </>
      }
    >
      <div className="flex flex-col gap-5">
        {infoMessage ? <Alert tone="info">{infoMessage}</Alert> : null}
        {devError ? <Alert>{devError}</Alert> : null}

        {SHOW_MOBILE_OTP_LOGIN ? (
          <Segmented name="sign-in-mode" label="Sign-in method" options={SIGN_IN_MODES} value={mode} onChange={setMode} />
        ) : null}

        {mode === "otp" ? <OtpLogin /> : <PasswordLogin />}

        {DEV_AUTH_ENABLED ? (
          <Button
            type="button"
            variant="ghost"
            fullWidth
            loading={devSigningIn}
            onClick={devSignIn}
            className="border border-dashed border-amber-500 text-amber-600 dark:text-amber-400"
          >
            Dev sign in (test provider) — local only, skips OTP
          </Button>
        ) : null}
      </div>
    </AuthShell>
  );
}

function OtpLogin() {
  const router = useRouter();
  const [step, setStep] = useState<"mobile" | "otp">("mobile");
  const [mobile, setMobile] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [isResending, setIsResending] = useState(false);
  const resend = useResendCountdown();

  const mobileForm = useForm<MobileFormValues>({
    resolver: zodResolver(mobileSchema),
    defaultValues: { mobile: "" },
  });

  const otpForm = useForm<OtpFormValues>({
    resolver: zodResolver(otpSchema),
    defaultValues: { otpCode: "" },
  });

  const requestOtp = mobileForm.handleSubmit(async (values) => {
    setError(null);
    try {
      await requestLoginOtp({ mobile: values.mobile });
      setMobile(values.mobile);
      setNotice(`We sent a verification code to ${values.mobile}.`);
      setStep("otp");
      otpForm.reset({ otpCode: "" });
      resend.start();
    } catch (err) {
      setError(describeError(err));
    }
  });

  const verifyOtp = otpForm.handleSubmit(async (values) => {
    setError(null);
    try {
      const session = await verifyLoginOtp({ mobile, otpCode: values.otpCode });
      storeSession(session);
      router.push("/today");
    } catch (err) {
      setError(describeLoginError(err));
    }
  });

  // Resend is the same request as the first send - the countdown, not a
  // different endpoint, is what stops a provider burning their SMS quota.
  const resendOtp = async () => {
    setError(null);
    setIsResending(true);
    try {
      await requestLoginOtp({ mobile });
      setNotice(`We sent a new verification code to ${mobile}.`);
      resend.start();
    } catch (err) {
      setError(describeError(err));
    } finally {
      setIsResending(false);
    }
  };

  const changeNumber = () => {
    setStep("mobile");
    setError(null);
    setNotice(null);
    otpForm.reset({ otpCode: "" });
  };

  if (step === "mobile") {
    return (
      <form method="post" onSubmit={requestOtp} className="flex flex-col gap-4" noValidate>
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
          inputMode="tel"
          autoComplete="tel"
          autoFocus
          placeholder="+919876543210"
          error={mobileForm.formState.errors.mobile?.message}
          {...mobileForm.register("mobile")}
        />
        <Button type="submit" size="lg" fullWidth loading={mobileForm.formState.isSubmitting}>
          Send verification code
        </Button>
      </form>
    );
  }

  return (
    <form method="post" onSubmit={verifyOtp} className="flex flex-col gap-4" noValidate>
      {error ? <Alert>{error}</Alert> : null}
      {notice ? <Alert tone="info">{notice}</Alert> : null}

      <OtpInput
        autoFocus
        error={otpForm.formState.errors.otpCode?.message}
        {...otpForm.register("otpCode")}
      />
      <Button type="submit" size="lg" fullWidth loading={otpForm.formState.isSubmitting}>
        Sign in
      </Button>

      <ResendRow
        remaining={resend.remaining}
        canResend={resend.canResend}
        onResend={resendOtp}
        pending={isResending}
      />

      <Button type="button" variant="ghost" fullWidth onClick={changeNumber}>
        Use a different number
      </Button>
    </form>
  );
}

function PasswordLogin() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [passwordVisible, setPasswordVisible] = useState(false);

  const form = useForm<PasswordFormValues>({
    resolver: zodResolver(passwordSchema),
    defaultValues: { email: "", password: "" },
  });

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    try {
      const session = await loginWithPassword(values);
      storeSession(session);
      // /install-app shows the "add to home screen" steps on a mobile
      // browser that hasn't seen them before, then forwards on to /today
      // itself - see that page for the skip conditions.
      router.push("/install-app?next=%2Ftoday");
    } catch (err) {
      setError(describeLoginError(err, "password"));
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
      <div className="relative">
        <Field
          label="Password"
          type={passwordVisible ? "text" : "password"}
          autoComplete="current-password"
          className="pr-11"
          error={form.formState.errors.password?.message}
          {...form.register("password")}
        />
        <IconButton
          type="button"
          label={passwordVisible ? "Hide password" : "Show password"}
          onClick={() => setPasswordVisible((visible) => !visible)}
          className="absolute right-1 top-[30px]"
        >
          {passwordVisible ? <EyeOffIcon /> : <EyeIcon />}
        </IconButton>
      </div>
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

/* Line icons matching the rest of the kit's stroke style (viewBox 24, currentColor, 2px stroke). */

function EyeIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className="h-4 w-4" aria-hidden>
      <path d="M2.5 12S6 5 12 5s9.5 7 9.5 7-3.5 7-9.5 7-9.5-7-9.5-7Z" />
      <circle cx="12" cy="12" r="3" />
    </svg>
  );
}

function EyeOffIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className="h-4 w-4" aria-hidden>
      <path d="M3 3l18 18" />
      <path d="M10.6 5.1A10.4 10.4 0 0 1 12 5c6 0 9.5 7 9.5 7a15.6 15.6 0 0 1-3.06 3.9M6.5 6.5C3.8 8.2 2.5 12 2.5 12s3.5 7 9.5 7a9.6 9.6 0 0 0 4.24-.96" />
      <path d="M9.9 9.9a3 3 0 0 0 4.2 4.2" />
    </svg>
  );
}
