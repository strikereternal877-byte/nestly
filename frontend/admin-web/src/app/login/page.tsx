"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { AuthShell } from "@/components/auth-ui";
import { Alert, Button, Field } from "@/components/ui";
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
