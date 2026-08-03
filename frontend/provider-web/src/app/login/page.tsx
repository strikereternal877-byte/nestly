"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Alert, Button, Card, Field, PageHeading } from "@/components/ui";
import { describeError, describeLoginError } from "@/lib/api";
import { requestLoginOtp, verifyLoginOtp } from "@/lib/auth-api";
import { isAuthenticated, storeSession, subscribeToAuthChanges } from "@/lib/auth";

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

/**
 * Provider sign-in (docs/PROVIDER.md's OTP-based auth): mobile number entry
 * requests an OTP, then the provider enters that code to obtain a session.
 * Two independent forms rather than one, one per step - it keeps each
 * step's validation and submit handler simple instead of one form juggling
 * two very different "what does submit do here" meanings.
 */
export default function ProviderLoginPage() {
  const router = useRouter();
  const [step, setStep] = useState<"mobile" | "otp">("mobile");
  const [mobile, setMobile] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [infoMessage, setInfoMessage] = useState<string | null>(null);

  // Already signed in (e.g. back-button to /login with a live session) -
  // send straight to the jobs list instead of showing the form again.
  useEffect(() => {
    const sync = () => {
      if (isAuthenticated()) router.replace("/jobs");
    };
    sync();
    return subscribeToAuthChanges(sync);
  }, [router]);

  // Read the query string directly (rather than next/navigation's
  // useSearchParams) so this page can stay statically prerendered - a
  // useSearchParams call forces the whole page out of static rendering
  // unless wrapped in its own Suspense boundary, which is unnecessary
  // machinery for a single one-off banner.
  useEffect(() => {
    if (typeof window === "undefined") return;
    if (new URLSearchParams(window.location.search).get("registered") === "1") {
      setInfoMessage("Registration submitted. Enter your mobile number to sign in.");
    }
  }, []);

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
      setInfoMessage(`We sent a verification code to ${values.mobile}.`);
      setStep("otp");
      otpForm.reset({ otpCode: "" });
    } catch (err) {
      setError(describeError(err));
    }
  });

  const verifyOtp = otpForm.handleSubmit(async (values) => {
    setError(null);
    try {
      const session = await verifyLoginOtp({ mobile, otpCode: values.otpCode });
      storeSession(session);
      router.push("/jobs");
    } catch (err) {
      setError(describeLoginError(err));
    }
  });

  const changeNumber = () => {
    setStep("mobile");
    setError(null);
    setInfoMessage(null);
    otpForm.reset({ otpCode: "" });
  };

  return (
    <main className="mx-auto flex min-h-screen w-full max-w-md flex-col justify-center px-6 py-12">
      <PageHeading
        title="Provider sign in"
        subtitle="Sign in with your registered mobile number."
      />

      <Card title={step === "mobile" ? "Enter your mobile number" : "Enter the verification code"}>
        {error ? (
          <div className="mb-4">
            <Alert>{error}</Alert>
          </div>
        ) : null}
        {infoMessage ? (
          <div className="mb-4">
            <Alert tone="info">{infoMessage}</Alert>
          </div>
        ) : null}

        {step === "mobile" ? (
          <form onSubmit={requestOtp} className="flex flex-col gap-4" noValidate>
            <Field
              label="Mobile number"
              type="tel"
              autoComplete="tel"
              placeholder="e.g. 9876543210"
              error={mobileForm.formState.errors.mobile?.message}
              {...mobileForm.register("mobile")}
            />
            <Button type="submit" disabled={mobileForm.formState.isSubmitting}>
              {mobileForm.formState.isSubmitting ? "Sending code…" : "Send verification code"}
            </Button>
          </form>
        ) : (
          <form onSubmit={verifyOtp} className="flex flex-col gap-4" noValidate>
            <Field
              label="Verification code"
              type="text"
              inputMode="numeric"
              autoComplete="one-time-code"
              error={otpForm.formState.errors.otpCode?.message}
              {...otpForm.register("otpCode")}
            />
            <Button type="submit" disabled={otpForm.formState.isSubmitting}>
              {otpForm.formState.isSubmitting ? "Verifying…" : "Sign in"}
            </Button>
            <Button type="button" variant="secondary" onClick={changeNumber}>
              Use a different number
            </Button>
          </form>
        )}
      </Card>

      <p className="mt-6 text-center text-sm text-neutral-600 dark:text-neutral-400">
        New provider?{" "}
        <Link href="/register" className="underline underline-offset-2">
          Register here
        </Link>
        .
      </p>
    </main>
  );
}
