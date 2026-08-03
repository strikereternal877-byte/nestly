"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import {
  AuthShell,
  OtpField,
  ResendRow,
  Segmented,
  useResendCountdown,
} from "@/components/auth-ui";
import { Alert, Button, Field } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { storeSession } from "@/lib/auth";
import type { LoginResponse } from "@/lib/types";
import {
  ADMIN_WEB_URL,
  PROVIDER_WEB_URL,
  loginAdmin,
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
const adminPasswordSchema = passwordSchema;
const providerMobileSchema = z.object({ mobile: mobileSchema });
const providerOtpSchema = z.object({
  mobile: mobileSchema,
  otpCode: z.string().min(4, "Enter the code you received").max(8, "Enter the code you received"),
});

type Mode = "otp" | "password";
type AccountType = "customer" | "admin" | "provider";

const ACCOUNT_TYPES = [
  { value: "customer" as const, label: "Customer" },
  { value: "admin" as const, label: "Admin" },
  { value: "provider" as const, label: "Provider" },
];

const SIGN_IN_MODES = [
  { value: "otp" as const, label: "Mobile OTP" },
  { value: "password" as const, label: "Email & password" },
];

/**
 * Single sign-in entry point for all three Nestly apps (task 206). Before
 * this, customer-web, admin-web and provider-web each had their own
 * independent `/login` at their own origin with no way to reach the other
 * two from one place. There is no shared parent domain across the three
 * origins yet (docs/DEVOPS.md's hosting/domain decisions are still open),
 * so admin/provider sign-in still authenticates against admin-api/provider-api
 * directly from here, then hands the browser off to that app's own origin
 * with the session in the URL fragment (see lib/unified-login-api.ts)
 * rather than a subdomain-gateway/shared-cookie approach, which real infra
 * doesn't exist to support yet. Each backend keeps issuing its own
 * independently-audienced token exactly as before - only the routing to
 * reach it is shared.
 *
 * admin-web's and provider-web's own `/login` pages are intentionally left in
 * place (not removed) so a bookmarked/direct visit to either app's own
 * origin still works.
 */
export default function LoginPage() {
  const [accountType, setAccountType] = useState<AccountType>("customer");
  const [mode, setMode] = useState<Mode>("otp");

  return (
    <AuthShell
      title="Welcome back"
      subtitle="One sign-in for customers, admins and providers."
      footer={
        accountType === "customer" ? (
          <>
            New to Nestly?{" "}
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
            <Segmented
              name="sign-in-mode"
              label="Sign-in method"
              options={SIGN_IN_MODES}
              value={mode}
              onChange={setMode}
            />
            {mode === "otp" ? <OtpLogin /> : <PasswordLogin />}
          </>
        ) : accountType === "admin" ? (
          <AdminLoginUnified />
        ) : (
          <ProviderLoginUnified />
        )}
      </div>
    </AuthShell>
  );
}

function OtpLogin() {
  const router = useRouter();
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
      router.push("/profile");
    } catch (err) {
      setError(describeError(err));
    }
  });

  if (step === "request") {
    return (
      <form onSubmit={onRequest} className="flex flex-col gap-4" noValidate>
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
    <form onSubmit={onVerify} className="flex flex-col gap-4" noValidate>
      {error ? <Alert>{error}</Alert> : null}
      {notice ? <Alert tone="info">{notice}</Alert> : null}

      <OtpField
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
      router.push("/profile");
    } catch (err) {
      setError(describeError(err));
    }
  });

  return (
    <form onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
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

/** Admin sign-in from the unified entry point - calls admin-api directly, then hands off to admin-web's own origin. */
function AdminLoginUnified() {
  const [error, setError] = useState<string | null>(null);

  const form = useForm<z.infer<typeof adminPasswordSchema>>({
    resolver: zodResolver(adminPasswordSchema),
    defaultValues: { email: "", password: "" },
  });

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    try {
      const session = await loginAdmin(values.email, values.password);
      redirectWithSession(ADMIN_WEB_URL, "/dashboard", session);
    } catch (err) {
      setError(describeError(err));
    }
  });

  return (
    <form onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
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
        Sign in to admin
      </Button>
      <p className="text-center text-xs text-fg-subtle">
        You&apos;ll be taken to the admin panel on its own address.
      </p>
    </form>
  );
}

/** Provider sign-in from the unified entry point - calls provider-api directly, then hands off to provider-web's own origin. */
function ProviderLoginUnified() {
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
      <form onSubmit={onRequest} className="flex flex-col gap-4" noValidate>
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
    <form onSubmit={onVerify} className="flex flex-col gap-4" noValidate>
      {error ? <Alert>{error}</Alert> : null}
      {notice ? <Alert tone="info">{notice}</Alert> : null}

      {/* Provider codes are 4-8 digits (providerOtpSchema), unlike the
          customer flow's fixed 6. */}
      <OtpField
        length={8}
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
