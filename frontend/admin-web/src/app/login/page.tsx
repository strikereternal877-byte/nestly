"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { AuthShell } from "@/components/auth-ui";
import { Alert, Button, Field, IconButton } from "@/components/ui";
import { API_V1, apiFetch, describeLoginError } from "@/lib/api";
import { isAuthenticated, storeSession, subscribeToAuthChanges } from "@/lib/auth";
import type { AdminLoginResponse } from "@/lib/types";

// Basic shape validation before spending a request; the server remains the
// authority on the real password policy (SRS 12.1.1) - this only catches
// empty/malformed input early.
const loginSchema = z.object({
  email: z.email("Enter a valid email address"),
  password: z.string().min(1, "Password is required"),
});

type LoginFormValues = z.infer<typeof loginSchema>;

/**
 * admin-web's own sign-in page. Deliberately kept alongside the unified entry
 * point on customer-web (task 206) so a bookmarked or direct visit to this
 * app's origin still works.
 */
export default function AdminLoginPage() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [passwordVisible, setPasswordVisible] = useState(false);

  // Already signed in (e.g. back-button to /login with a live session) -
  // send straight to the dashboard instead of showing the form again.
  useEffect(() => {
    const sync = () => {
      if (isAuthenticated()) router.replace("/dashboard");
    };
    sync();
    return subscribeToAuthChanges(sync);
  }, [router]);

  const form = useForm<LoginFormValues>({
    resolver: zodResolver(loginSchema),
    defaultValues: { email: "", password: "" },
  });

  const onSubmit = form.handleSubmit(async (values) => {
    setError(null);
    try {
      const session = await apiFetch<AdminLoginResponse>(`${API_V1}/auth/login`, {
        method: "POST",
        body: JSON.stringify(values),
      });
      storeSession(session);
      router.push("/dashboard");
    } catch (err) {
      setError(describeLoginError(err));
    }
  });

  return (
    <AuthShell
      title="Sign in"
      subtitle="Use your admin email and password."
      footer="Authorised personnel only. Activity on this panel is audited."
    >
      <form method="post" onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
        {/* method="post" is defence in depth, not routing: react-hook-form's
            handleSubmit preventDefaults every real submit, so this attribute never
            takes effect once the page is interactive. It matters for a submit that
            lands *before* hydration (slow JS, a failed chunk, an extension), which
            falls back to the browser's native behaviour - and a form with no method
            defaults to GET, which would put the admin password into the URL, the
            browser history, the server access log and any outbound Referer header.
            POST keeps them in a request body. */}
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
        {/* Row 71 in docs/OPEN-FIXES-FEATURES.csv: there was no entry point
            on this form toward a password reset at all. admin-api has no
            self-service "forgot password" endpoint (only /admin/auth/login
            and an admin-initiated reset under /admin-users that requires
            already being signed in as a Super Admin - AdminUsersController's
            ResetPassword) - unlike customer-web/provider-web's OTP-based
            reset, standing that up here would be new backend work, out of
            scope for this pass. This points at the reset path that does
            exist today instead of linking to a self-service page that
            isn't there. */}
        <p className="text-center text-sm text-fg-muted">
          Forgot your password? Ask a Super Admin to reset it from Admin Users.
        </p>
      </form>
    </AuthShell>
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
