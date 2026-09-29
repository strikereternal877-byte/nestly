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
  useResendCountdown,
} from "@/components/auth-ui";
import { OtpInput } from "@/components/OtpInput";
import { Alert, Button, Checkbox, Field } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import type { CustomerSummary } from "@/lib/types";

// Mirrors RegistrationValidators.cs (RegisterCustomerWithEmailRequestValidator).
const mobileSchema = z
  .string()
  .regex(/^\+?[1-9]\d{7,14}$/, "Enter a valid mobile number");

const emailOtpRequestSchema = z.object({
  email: z.email("Enter a valid email address"),
});

const registerSchema = z.object({
  email: z.email("Enter a valid email address"),
  otpCode: z.string().regex(/^\d{6}$/, "Enter the 6-digit code"),
  name: z.string().min(1, "Name is required").max(200),
  mobile: mobileSchema,
  password: z.string().min(8, "Password must be at least 8 characters"),
  consentAccepted: z.literal(true, {
    message: "You must accept the Terms & Privacy Policy",
  }),
  referralCode: z.string(),
});

export default function RegisterPage() {
  // Suspense for useSearchParams below (see login/page.tsx for the same
  // pattern): reading the `?ref=` invite code opts this tree out of static
  // rendering, which the App Router requires a boundary around.
  return (
    <Suspense
      fallback={
        <AuthShell title="Create your account" subtitle="We verify your email with a one-time code.">
          <div />
        </AuthShell>
      }
    >
      <RegisterScreen />
    </Suspense>
  );
}

function RegisterScreen() {
  const router = useRouter();
  // Shared referral links carry the referrer's code as `?ref=` (see
  // ReferralOptions.ShareLinkBaseUrl).
  const referralCodeFromLink = useSearchParams().get("ref") ?? "";
  const [step, setStep] = useState<"otp" | "details">("otp");
  const [email, setEmail] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [resending, setResending] = useState(false);
  const [registered, setRegistered] = useState(false);
  const { remaining, start, canResend } = useResendCountdown();

  const otpForm = useForm<z.infer<typeof emailOtpRequestSchema>>({
    resolver: zodResolver(emailOtpRequestSchema),
    defaultValues: { email: "" },
  });

  const detailsForm = useForm<z.infer<typeof registerSchema>>({
    resolver: zodResolver(registerSchema),
    defaultValues: {
      email: "",
      otpCode: "",
      name: "",
      mobile: "",
      password: "",
      // Must start false. Defaulting to true pre-ticks the consent box, so the
      // schema's "you must accept" rule can never fire and the account is
      // created recording a consent the customer never actually gave.
      consentAccepted: false as unknown as true,
      referralCode: referralCodeFromLink,
    },
  });

  const sendCode = async (value: string) => {
    await apiFetch(`${API_V1}/auth/registration/email-otp`, {
      method: "POST",
      body: JSON.stringify({ email: value }),
    });
    start();
  };

  const onRequestOtp = otpForm.handleSubmit(async ({ email: value }) => {
    setError(null);
    try {
      await sendCode(value);
      setEmail(value);
      detailsForm.setValue("email", value);
      setNotice(`We sent a 6-digit code to ${value}.`);
      setStep("details");
    } catch (err) {
      setError(describeError(err));
    }
  });

  const onResend = async () => {
    setError(null);
    setResending(true);
    try {
      await sendCode(email);
      setNotice(`We sent a new code to ${email}.`);
    } catch (err) {
      setError(describeError(err));
    } finally {
      setResending(false);
    }
  };

  const onRegister = detailsForm.handleSubmit(async (values) => {
    setError(null);
    try {
      await apiFetch<CustomerSummary>(`${API_V1}/auth/registration/email`, {
        method: "POST",
        body: JSON.stringify({
          email: values.email,
          otpCode: values.otpCode,
          name: values.name,
          mobile: values.mobile,
          password: values.password,
          consentAccepted: values.consentAccepted,
          referralCode: values.referralCode === "" ? null : values.referralCode,
        }),
      });
      // A referral code was submitted: pause on a confirmation instead of an
      // immediate redirect. The server processes referrals best-effort
      // (CustomerRegistrationService.TryCreateReferralAsync) and never
      // reports back whether the code matched, so this can only confirm the
      // code was submitted, not that a reward was created.
      if (values.referralCode !== "") {
        setRegistered(true);
      } else {
        // Registration doesn't return a session, so sign-in happens next -
        // the install-app screen carries the eventual /login destination
        // along as `next` and forwards it once the customer is done there.
        router.push("/install-app?next=%2Flogin");
      }
    } catch (err) {
      setError(describeError(err));
    }
  });

  return (
    <AuthShell
      title="Create your account"
      subtitle="We verify your email with a one-time code."
      footer={
        <>
          Already have an account?{" "}
          <Link
            href="/login"
            className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
          >
            Sign in
          </Link>
        </>
      }
    >
      {registered ? (
        <div className="flex flex-col gap-4">
          <Alert tone="success">
            Account created — you were invited by a friend. If your code was
            valid, their reward will be added once it qualifies.
          </Alert>
          <Button size="lg" fullWidth onClick={() => router.push("/install-app?next=%2Flogin")}>
            Continue
          </Button>
        </div>
      ) : step === "otp" ? (
        <form method="post" onSubmit={onRequestOtp} className="flex flex-col gap-4" noValidate>
          {/* method="post" is defence in depth, not routing: react-hook-form's
              handleSubmit preventDefaults every real submit, so this attribute never
              takes effect once the page is interactive. It matters for a submit that
              lands *before* hydration (slow JS, a failed chunk, an extension), which
              falls back to the browser's native behaviour - and a form with no method
              defaults to GET, which would put the OTP and chosen password into the URL, the
              browser history, the server access log and any outbound Referer header.
              POST keeps them in a request body. */}
          {error ? <Alert>{error}</Alert> : null}
          <Field
            label="Email"
            type="email"
            autoComplete="email"
            placeholder="you@example.com"
            hint="We'll email you a 6-digit code to confirm it's you."
            error={otpForm.formState.errors.email?.message}
            {...otpForm.register("email")}
          />
          <Button type="submit" size="lg" fullWidth loading={otpForm.formState.isSubmitting}>
            Send code
          </Button>
        </form>
      ) : (
        <form method="post" onSubmit={onRegister} className="flex flex-col gap-4" noValidate>
          {error ? <Alert>{error}</Alert> : null}
          {notice ? <Alert tone="info">{notice}</Alert> : null}

          <OtpInput
            error={detailsForm.formState.errors.otpCode?.message}
            {...detailsForm.register("otpCode")}
          />

          <ResendRow
            remaining={remaining}
            canResend={canResend}
            onResend={onResend}
            pending={resending}
          />

          <div className="my-1 border-t border-line" />

          <Field
            label="Full name"
            required
            autoComplete="name"
            error={detailsForm.formState.errors.name?.message}
            {...detailsForm.register("name")}
          />
          <Field
            label="Mobile number"
            type="tel"
            autoComplete="tel"
            placeholder="+919876543210"
            hint="For booking updates and provider contact."
            error={detailsForm.formState.errors.mobile?.message}
            {...detailsForm.register("mobile")}
          />
          <Field
            label="Password"
            type="password"
            autoComplete="new-password"
            hint="At least 8 characters."
            error={detailsForm.formState.errors.password?.message}
            {...detailsForm.register("password")}
          />

          <Field
            label="Referral code"
            autoComplete="off"
            hint="Optional — from a friend's invite link."
            error={detailsForm.formState.errors.referralCode?.message}
            {...detailsForm.register("referralCode")}
          />

          <div className="flex flex-col gap-1.5">
            <Checkbox
              label="I accept the Terms & Privacy Policy"
              {...detailsForm.register("consentAccepted")}
            />
            {detailsForm.formState.errors.consentAccepted ? (
              <p className="text-xs font-medium text-danger">
                {detailsForm.formState.errors.consentAccepted.message}
              </p>
            ) : null}
          </div>

          <Button type="submit" size="lg" fullWidth loading={detailsForm.formState.isSubmitting}>
            Create account
          </Button>

          <Button
            type="button"
            variant="ghost"
            onClick={() => {
              setStep("otp");
              setNotice(null);
              setError(null);
            }}
          >
            Use a different email ({email})
          </Button>
        </form>
      )}
    </AuthShell>
  );
}
