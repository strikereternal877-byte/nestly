"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Alert, Button, Card, CheckboxField, Field, PageHeading } from "@/components/ui";
import { describeError } from "@/lib/api";
import { registerProvider, requestRegistrationOtp } from "@/lib/auth-api";

const mobileSchema = z.object({
  mobile: z
    .string()
    .min(7, "Enter a valid mobile number")
    .max(15, "Enter a valid mobile number")
    .regex(/^[0-9+]+$/, "Digits only (a leading + is fine)"),
});
type MobileFormValues = z.infer<typeof mobileSchema>;

const detailsSchema = z.object({
  otpCode: z
    .string()
    .min(4, "Enter the code you received")
    .max(8, "Enter the code you received")
    .regex(/^[0-9]+$/, "The code is numeric"),
  legalName: z.string().min(1, "Legal name is required").max(200),
  displayName: z.string().min(1, "Display name is required").max(100),
  email: z.union([z.email("Enter a valid email address"), z.literal("")]),
  consentAccepted: z.literal(true, {
    error: "You must accept the terms to register.",
  }),
});
type DetailsFormValues = z.infer<typeof detailsSchema>;

/**
 * Provider registration (docs/PROVIDER.md's OTP-based auth): mobile number
 * entry requests an OTP, then the provider supplies their details alongside
 * that code in a single submission (matching the API contract - registration
 * itself does not return a session, so a successful registration sends the
 * provider to /login to sign in with a fresh OTP).
 */
export default function ProviderRegisterPage() {
  const router = useRouter();
  const [step, setStep] = useState<"mobile" | "details">("mobile");
  const [mobile, setMobile] = useState("");
  const [error, setError] = useState<string | null>(null);

  const mobileForm = useForm<MobileFormValues>({
    resolver: zodResolver(mobileSchema),
    defaultValues: { mobile: "" },
  });

  const detailsForm = useForm<DetailsFormValues>({
    resolver: zodResolver(detailsSchema),
    defaultValues: {
      otpCode: "",
      legalName: "",
      displayName: "",
      email: "",
      consentAccepted: false as unknown as true,
    },
  });

  const requestOtp = mobileForm.handleSubmit(async (values) => {
    setError(null);
    try {
      await requestRegistrationOtp({ mobile: values.mobile });
      setMobile(values.mobile);
      setStep("details");
    } catch (err) {
      setError(describeError(err));
    }
  });

  const submitRegistration = detailsForm.handleSubmit(async (values) => {
    setError(null);
    try {
      await registerProvider({
        mobile,
        otpCode: values.otpCode,
        legalName: values.legalName,
        displayName: values.displayName,
        email: values.email === "" ? undefined : values.email,
        consentAccepted: values.consentAccepted,
      });
      router.push("/login?registered=1");
    } catch (err) {
      setError(describeError(err));
    }
  });

  const changeNumber = () => {
    setStep("mobile");
    setError(null);
    detailsForm.reset();
  };

  return (
    <main className="mx-auto flex min-h-screen w-full max-w-md flex-col justify-center px-6 py-12">
      <PageHeading
        title="Become a Nestly provider"
        subtitle="Register with your mobile number to start onboarding."
      />

      <Card title={step === "mobile" ? "Enter your mobile number" : "Verify and complete your profile"}>
        {error ? (
          <div className="mb-4">
            <Alert>{error}</Alert>
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
          <form onSubmit={submitRegistration} className="flex flex-col gap-4" noValidate>
            <p className="text-sm text-neutral-600 dark:text-neutral-400">
              We sent a verification code to {mobile}.
            </p>
            <Field
              label="Verification code"
              type="text"
              inputMode="numeric"
              autoComplete="one-time-code"
              error={detailsForm.formState.errors.otpCode?.message}
              {...detailsForm.register("otpCode")}
            />
            <Field
              label="Legal name"
              error={detailsForm.formState.errors.legalName?.message}
              {...detailsForm.register("legalName")}
            />
            <Field
              label="Display name"
              error={detailsForm.formState.errors.displayName?.message}
              {...detailsForm.register("displayName")}
            />
            <Field
              label="Email (optional)"
              type="email"
              autoComplete="email"
              error={detailsForm.formState.errors.email?.message}
              {...detailsForm.register("email")}
            />
            <CheckboxField
              label="I accept the provider terms and conditions"
              checked={detailsForm.watch("consentAccepted") === true}
              onChange={(checked) => detailsForm.setValue("consentAccepted", checked as unknown as true)}
            />
            {detailsForm.formState.errors.consentAccepted ? (
              <p className="text-xs text-red-600 dark:text-red-400">
                {detailsForm.formState.errors.consentAccepted.message}
              </p>
            ) : null}
            <Button type="submit" disabled={detailsForm.formState.isSubmitting}>
              {detailsForm.formState.isSubmitting ? "Registering…" : "Complete registration"}
            </Button>
            <Button type="button" variant="secondary" onClick={changeNumber}>
              Use a different number
            </Button>
          </form>
        )}
      </Card>

      <p className="mt-6 text-center text-sm text-neutral-600 dark:text-neutral-400">
        Already registered?{" "}
        <Link href="/login" className="underline underline-offset-2">
          Sign in
        </Link>
        .
      </p>
    </main>
  );
}
