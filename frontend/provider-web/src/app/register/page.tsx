"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { AuthShell, ResendRow, useResendCountdown } from "@/components/auth-ui";
import { OtpInput } from "@/components/OtpInput";
import { Alert, Button, CheckboxField, Field } from "@/components/ui";
import { describeError } from "@/lib/api";
import { registerProviderWithEmail, requestRegistrationEmailOtp } from "@/lib/auth-api";

const emailSchema = z.object({
  email: z.email("Enter a valid email address"),
});
type EmailFormValues = z.infer<typeof emailSchema>;

const detailsSchema = z.object({
  otpCode: z
    .string()
    .min(4, "Enter the code you received")
    .max(8, "Enter the code you received")
    .regex(/^[0-9]+$/, "The code is numeric"),
  legalName: z.string().min(1, "Legal name is required").max(200),
  displayName: z.string().min(1, "Display name is required").max(100),
  mobile: z
    .string()
    .min(7, "Enter a valid mobile number")
    .max(15, "Enter a valid mobile number")
    .regex(/^[0-9+]+$/, "Digits only (a leading + is fine)"),
  password: z.string().min(8, "Password must be at least 8 characters"),
  consentAccepted: z.literal(true, {
    error: "You must accept the terms to register.",
  }),
});
type DetailsFormValues = z.infer<typeof detailsSchema>;

/**
 * Provider registration, email-first: an email address requests an OTP,
 * then the provider supplies their details alongside that code in a single
 * submission. Mobile is still collected and stored, but is no longer itself
 * OTP-verified (mirrors customer-web's own email-first registration).
 * Registration itself does not return a session, so a successful submission
 * sends the provider to /login to sign in with their new password.
 */
export default function ProviderRegisterPage() {
  const router = useRouter();
  const [step, setStep] = useState<"email" | "details">("email");
  const [email, setEmail] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [infoMessage, setInfoMessage] = useState<string | null>(null);
  const [isResending, setIsResending] = useState(false);
  const resend = useResendCountdown();

  const emailForm = useForm<EmailFormValues>({
    resolver: zodResolver(emailSchema),
    defaultValues: { email: "" },
  });

  const detailsForm = useForm<DetailsFormValues>({
    resolver: zodResolver(detailsSchema),
    defaultValues: {
      otpCode: "",
      legalName: "",
      displayName: "",
      mobile: "",
      password: "",
      // Deliberately false: pre-ticking a consent box records an agreement the
      // provider never gave, and would make the schema's z.literal(true) rule
      // unreachable.
      consentAccepted: false as unknown as true,
    },
  });

  const requestOtp = emailForm.handleSubmit(async (values) => {
    setError(null);
    try {
      await requestRegistrationEmailOtp({ email: values.email });
      setEmail(values.email);
      setInfoMessage(`We sent a verification code to ${values.email}.`);
      setStep("details");
      resend.start();
    } catch (err) {
      setError(describeError(err));
    }
  });

  const submitRegistration = detailsForm.handleSubmit(async (values) => {
    setError(null);
    try {
      await registerProviderWithEmail({
        email,
        otpCode: values.otpCode,
        legalName: values.legalName,
        displayName: values.displayName,
        mobile: values.mobile,
        password: values.password,
        consentAccepted: values.consentAccepted,
      });
      // /install-app shows the "add to home screen" steps on a mobile
      // browser that hasn't seen them before, then forwards on to /login.
      router.push("/install-app?next=%2Flogin%3Fregistered%3D1");
    } catch (err) {
      setError(describeError(err));
    }
  });

  // Same endpoint as the first send; the countdown is what prevents burning
  // through the resend limit and tripping gateway rate limiting.
  const resendOtp = async () => {
    setError(null);
    setIsResending(true);
    try {
      await requestRegistrationEmailOtp({ email });
      setInfoMessage(`We sent a new verification code to ${email}.`);
      resend.start();
    } catch (err) {
      setError(describeError(err));
    } finally {
      setIsResending(false);
    }
  };

  const changeEmail = () => {
    setStep("email");
    setError(null);
    setInfoMessage(null);
    detailsForm.reset();
  };

  const consentError = detailsForm.formState.errors.consentAccepted?.message;

  return (
    <AuthShell
      title="Become a Glavyx provider"
      subtitle={
        step === "email"
          ? "Register with your email to start onboarding."
          : "Verify your email and tell us who you are."
      }
      footer={
        <>
          Already registered?{" "}
          <Link
            href="/login"
            className="font-medium text-brand-600 underline-offset-4 hover:underline dark:text-brand-400"
          >
            Sign in
          </Link>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {error ? <Alert>{error}</Alert> : null}
        {infoMessage && !error ? <Alert tone="info">{infoMessage}</Alert> : null}

        {step === "email" ? (
          <form method="post" onSubmit={requestOtp} className="flex flex-col gap-4" noValidate>
            {/* method="post" is defence in depth, not routing: react-hook-form's
                handleSubmit preventDefaults every real submit, so this attribute never
                takes effect once the page is interactive. It matters for a submit that
                lands *before* hydration (slow JS, a failed chunk, an extension), which
                falls back to the browser's native behaviour - and a form with no method
                defaults to GET, which would put the OTP and chosen password into the URL, the
                browser history, the server access log and any outbound Referer header.
                POST keeps them in a request body. */}
            <Field
              label="Email"
              type="email"
              inputMode="email"
              autoComplete="email"
              autoFocus
              placeholder="you@example.com"
              error={emailForm.formState.errors.email?.message}
              {...emailForm.register("email")}
            />
            <Button type="submit" size="lg" fullWidth loading={emailForm.formState.isSubmitting}>
              Send verification code
            </Button>
            <p className="text-center text-xs text-fg-muted">
              We&apos;ll email a 6-digit code to confirm this address, then ask for a few
              details to finish setting up your account.
            </p>
          </form>
        ) : (
          <form method="post" onSubmit={submitRegistration} className="flex flex-col gap-5" noValidate>
            <OtpInput
              autoFocus
              error={detailsForm.formState.errors.otpCode?.message}
              {...detailsForm.register("otpCode")}
            />

            <ResendRow
              remaining={resend.remaining}
              canResend={resend.canResend}
              onResend={resendOtp}
              pending={isResending}
            />

            <div className="flex flex-col gap-4 border-t border-line pt-5">
              <Field
                label="Legal name"
                hint="As it appears on your identity documents."
                autoComplete="name"
                error={detailsForm.formState.errors.legalName?.message}
                {...detailsForm.register("legalName")}
              />
              <Field
                label="Display name"
                hint="What customers will see."
                error={detailsForm.formState.errors.displayName?.message}
                {...detailsForm.register("displayName")}
              />
              <Field
                label="Mobile number"
                type="tel"
                inputMode="tel"
                autoComplete="tel"
                placeholder="+919876543210"
                hint="For job updates and customer contact."
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

              <div className="flex flex-col gap-1.5">
                <CheckboxField
                  label="I accept the provider terms and conditions"
                  description="Covers how jobs are assigned, how you are paid, and how your data is used."
                  checked={detailsForm.watch("consentAccepted") === true}
                  onChange={(checked) =>
                    detailsForm.setValue("consentAccepted", checked as unknown as true, {
                      shouldValidate: true,
                    })
                  }
                />
                {consentError ? (
                  <p className="text-xs font-medium text-danger">{consentError}</p>
                ) : null}
              </div>
            </div>

            <Button type="submit" size="lg" fullWidth loading={detailsForm.formState.isSubmitting}>
              Complete registration
            </Button>
            <Button type="button" variant="ghost" fullWidth onClick={changeEmail}>
              Use a different email
            </Button>
          </form>
        )}
      </div>
    </AuthShell>
  );
}
