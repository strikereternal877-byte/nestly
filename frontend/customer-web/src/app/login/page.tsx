"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Alert, Button, Card, Field, PageHeading } from "@/components/ui";
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

const ACCOUNT_TYPES: { value: AccountType; label: string }[] = [
  { value: "customer", label: "Customer" },
  { value: "admin", label: "Admin" },
  { value: "provider", label: "Provider" },
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
    <main className="mx-auto w-full max-w-md px-6 py-12">
      <PageHeading title="Sign in" subtitle="One sign-in page for customers, admins and providers." />

      <div className="mb-5 flex gap-2" role="tablist" aria-label="Account type">
        {ACCOUNT_TYPES.map((type) => (
          <Button
            key={type.value}
            role="tab"
            aria-selected={accountType === type.value}
            variant={accountType === type.value ? "primary" : "secondary"}
            onClick={() => setAccountType(type.value)}
          >
            {type.label}
          </Button>
        ))}
      </div>

      {accountType === "customer" ? (
        <>
          <div className="mb-5 flex gap-2" role="tablist" aria-label="Sign-in method">
            <Button
              role="tab"
              aria-selected={mode === "otp"}
              variant={mode === "otp" ? "primary" : "secondary"}
              onClick={() => setMode("otp")}
            >
              Mobile OTP
            </Button>
            <Button
              role="tab"
              aria-selected={mode === "password"}
              variant={mode === "password" ? "primary" : "secondary"}
              onClick={() => setMode("password")}
            >
              Email &amp; password
            </Button>
          </div>

          {mode === "otp" ? <OtpLogin /> : <PasswordLogin />}

          <p className="mt-6 text-sm text-neutral-600 dark:text-neutral-400">
            New to Nestly?{" "}
            <Link href="/register" className="underline">
              Create an account
            </Link>
          </p>
        </>
      ) : accountType === "admin" ? (
        <AdminLoginUnified />
      ) : (
        <ProviderLoginUnified />
      )}
    </main>
  );
}

function OtpLogin() {
  const router = useRouter();
  const [step, setStep] = useState<"request" | "verify">("request");
  const [mobile, setMobile] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const requestForm = useForm<z.infer<typeof otpRequestSchema>>({
    resolver: zodResolver(otpRequestSchema),
    defaultValues: { mobile: "" },
  });

  const verifyForm = useForm<z.infer<typeof otpVerifySchema>>({
    resolver: zodResolver(otpVerifySchema),
    defaultValues: { mobile: "", otpCode: "" },
  });

  const onRequest = requestForm.handleSubmit(async ({ mobile: value }) => {
    setError(null);
    try {
      await apiFetch(`${API_V1}/auth/login/otp`, {
        method: "POST",
        body: JSON.stringify({ mobile: value }),
      });
      setMobile(value);
      verifyForm.setValue("mobile", value);
      setNotice(`We sent a 6-digit code to ${value}.`);
      setStep("verify");
    } catch (err) {
      setError(describeError(err));
    }
  });

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

  return (
    <Card title={step === "request" ? "Sign in with OTP" : "Enter your code"}>
      <div className="flex flex-col gap-4">
        {error ? <Alert>{error}</Alert> : null}
        {step === "verify" && notice ? <Alert tone="info">{notice}</Alert> : null}

        {step === "request" ? (
          <form onSubmit={onRequest} className="flex flex-col gap-4" noValidate>
            <Field
              label="Mobile number"
              type="tel"
              autoComplete="tel"
              placeholder="+919876543210"
              error={requestForm.formState.errors.mobile?.message}
              {...requestForm.register("mobile")}
            />
            <Button type="submit" disabled={requestForm.formState.isSubmitting}>
              {requestForm.formState.isSubmitting ? "Sending…" : "Send code"}
            </Button>
          </form>
        ) : (
          <form onSubmit={onVerify} className="flex flex-col gap-4" noValidate>
            <Field
              label="6-digit code"
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={6}
              error={verifyForm.formState.errors.otpCode?.message}
              {...verifyForm.register("otpCode")}
            />
            <Button type="submit" disabled={verifyForm.formState.isSubmitting}>
              {verifyForm.formState.isSubmitting ? "Verifying…" : "Verify and sign in"}
            </Button>
            <Button
              type="button"
              variant="secondary"
              onClick={() => {
                setStep("request");
                setNotice(null);
                setError(null);
              }}
            >
              Use a different number ({mobile})
            </Button>
          </form>
        )}
      </div>
    </Card>
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
    <Card title="Sign in with password">
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
        <Button type="submit" disabled={form.formState.isSubmitting}>
          {form.formState.isSubmitting ? "Signing in…" : "Sign in"}
        </Button>
        <Link href="/forgot-password" className="text-sm underline">
          Forgot your password?
        </Link>
      </form>
    </Card>
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
    <Card title="Admin sign in" description="Sign in with your admin email and password.">
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
        <Button type="submit" disabled={form.formState.isSubmitting}>
          {form.formState.isSubmitting ? "Signing in…" : "Sign in"}
        </Button>
      </form>
    </Card>
  );
}

/** Provider sign-in from the unified entry point - calls provider-api directly, then hands off to provider-web's own origin. */
function ProviderLoginUnified() {
  const [step, setStep] = useState<"request" | "verify">("request");
  const [mobile, setMobile] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

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
      setMobile(value);
      verifyForm.setValue("mobile", value);
      setNotice(`We sent a verification code to ${value}.`);
      setStep("verify");
    } catch (err) {
      setError(describeError(err));
    }
  });

  const onVerify = verifyForm.handleSubmit(async (values) => {
    setError(null);
    try {
      const session = await verifyProviderLoginOtp(values.mobile, values.otpCode);
      redirectWithSession(PROVIDER_WEB_URL, "/jobs", session);
    } catch (err) {
      setError(describeError(err));
    }
  });

  return (
    <Card title={step === "request" ? "Provider sign in" : "Enter your code"}>
      <div className="flex flex-col gap-4">
        {error ? <Alert>{error}</Alert> : null}
        {step === "verify" && notice ? <Alert tone="info">{notice}</Alert> : null}

        {step === "request" ? (
          <form onSubmit={onRequest} className="flex flex-col gap-4" noValidate>
            <Field
              label="Mobile number"
              type="tel"
              autoComplete="tel"
              placeholder="e.g. 9876543210"
              error={requestForm.formState.errors.mobile?.message}
              {...requestForm.register("mobile")}
            />
            <Button type="submit" disabled={requestForm.formState.isSubmitting}>
              {requestForm.formState.isSubmitting ? "Sending…" : "Send verification code"}
            </Button>
          </form>
        ) : (
          <form onSubmit={onVerify} className="flex flex-col gap-4" noValidate>
            <Field
              label="Verification code"
              inputMode="numeric"
              autoComplete="one-time-code"
              error={verifyForm.formState.errors.otpCode?.message}
              {...verifyForm.register("otpCode")}
            />
            <Button type="submit" disabled={verifyForm.formState.isSubmitting}>
              {verifyForm.formState.isSubmitting ? "Verifying…" : "Sign in"}
            </Button>
            <Button
              type="button"
              variant="secondary"
              onClick={() => {
                setStep("request");
                setNotice(null);
                setError(null);
              }}
            >
              Use a different number ({mobile})
            </Button>
          </form>
        )}
      </div>
    </Card>
  );
}
