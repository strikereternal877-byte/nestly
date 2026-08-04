"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { API_V1, apiFetch, describeLoginError } from "@/lib/api";
import { isAuthenticated, storeSession, subscribeToAuthChanges } from "@/lib/auth";
import type { AdminLoginResponse } from "@/lib/types";

function NestMark() {
  return (
    <svg viewBox="0 0 24 24" fill="none" className="h-6 w-6" aria-hidden="true">
      <path
        d="M4 11.5 12 4l8 7.5M6 10v9a1 1 0 0 0 1 1h4v-6h2v6h4a1 1 0 0 0 1-1v-9"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

function FacebookIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" className="h-4 w-4" aria-hidden="true">
      <path
        d="M14 9h2V6h-2c-1.7 0-3 1.3-3 3v2H9v3h2v6h3v-6h2l1-3h-3v-2c0-.6.4-1 1-1Z"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinejoin="round"
      />
    </svg>
  );
}

function TwitterIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" className="h-4 w-4" aria-hidden="true">
      <path
        d="M20 7c-.6.3-1.2.5-1.9.6.7-.4 1.2-1.1 1.4-1.9-.6.4-1.4.7-2.1.9A3.3 3.3 0 0 0 12 9.5c0 .3 0 .5.1.8-2.7-.1-5.2-1.4-6.8-3.4-.3.5-.4 1-.4 1.6 0 1.1.6 2.1 1.4 2.6-.5 0-1-.2-1.5-.4v.1c0 1.6 1.1 2.9 2.6 3.2-.3.1-.6.1-.9.1-.2 0-.4 0-.6-.1.4 1.3 1.6 2.2 3 2.3A6.6 6.6 0 0 1 4 17.9a9.3 9.3 0 0 0 5 1.5c6 0 9.3-5 9.3-9.3v-.4c.6-.5 1.2-1.1 1.7-1.7Z"
        stroke="currentColor"
        strokeWidth="1.3"
        strokeLinejoin="round"
      />
    </svg>
  );
}

function LinkedInIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" className="h-4 w-4" aria-hidden="true">
      <rect x="4" y="4" width="16" height="16" rx="2" stroke="currentColor" strokeWidth="1.5" />
      <path d="M8 10.5V17M8 7.5v.01M12 17v-4a1.8 1.8 0 0 1 3.6 0V17M12 13v4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
    </svg>
  );
}

// Basic shape validation before spending a request; the server remains the
// authority on the real password policy (SRS 12.1.1) - this only catches
// empty/malformed input early.
const loginSchema = z.object({
  email: z.email("Enter a valid email address"),
  password: z.string().min(1, "Password is required"),
});

type LoginFormValues = z.infer<typeof loginSchema>;

export default function AdminLoginPage() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [remember, setRemember] = useState(false);

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
    <main className="relative flex min-h-screen items-center justify-center overflow-hidden px-4 py-10">
      {/* Full-page backdrop: a warm, softly blurred gradient standing in for
          a photo (no stock image asset is bundled), dimmed under the card. */}
      <div className="absolute inset-0 bg-neutral-900" />
      <div
        className="absolute inset-0 opacity-90 blur-3xl"
        style={{
          background:
            "radial-gradient(circle at 25% 20%, #8a5a3f 0%, transparent 45%), radial-gradient(circle at 80% 15%, #5b4230 0%, transparent 40%), radial-gradient(circle at 50% 85%, #2c2420 0%, transparent 55%)",
        }}
      />
      <div className="absolute inset-0 bg-black/40" />

      {/*
       * Mirrors the reference template's .login-wrapper/.login-aside-left/
       * .login-aside-right structure: a left pane (brand, blurb, social,
       * footer) and a pink right pane (the form), side by side on desktop
       * and stacked on narrow viewports.
       */}
      <div className="relative flex w-full max-w-4xl flex-col overflow-hidden rounded-2xl shadow-2xl md:flex-row">
        <section className="relative flex flex-col justify-between overflow-hidden bg-white px-8 py-10 md:w-1/2 md:px-10 md:py-12">
          <div
            className="absolute inset-0 opacity-25 blur-2xl"
            style={{
              background:
                "radial-gradient(circle at 30% 20%, #c98a5f 0%, transparent 50%), radial-gradient(circle at 80% 70%, #8a5a3f 0%, transparent 50%)",
            }}
          />

          <div className="relative flex flex-col gap-10">
            <div className="flex items-center gap-3">
              <span className="flex h-11 w-11 items-center justify-center rounded-xl bg-pink-600 text-white shadow-sm">
                <NestMark />
              </span>
              <span className="text-2xl font-bold text-neutral-900">Nestly</span>
            </div>

            <div>
              <h2 className="text-3xl font-bold tracking-tight text-neutral-900">Command Center</h2>
              <p className="mt-3 max-w-sm text-sm text-neutral-600">
                Everything your team needs to run bookings, catalog, partners, and support
                — in one connected workspace.
              </p>
            </div>

            <div className="flex gap-3">
              {[FacebookIcon, TwitterIcon, LinkedInIcon].map((Icon, index) => (
                <span
                  key={index}
                  className="flex h-9 w-9 items-center justify-center rounded-full border border-pink-200 text-pink-600"
                >
                  <Icon />
                </span>
              ))}
            </div>
          </div>

          <div className="relative mt-10 flex flex-wrap items-center gap-x-5 gap-y-1 text-sm text-neutral-500">
            <span>Privacy Policy</span>
            <span>Contact</span>
            <span>&copy; {new Date().getFullYear()} Nestly</span>
          </div>
        </section>

        <section className="flex flex-col justify-center bg-pink-600 px-8 py-10 md:w-1/2 md:px-10 md:py-12">
          <div className="w-full">
            <h2 className="text-2xl font-bold text-white">Welcome to Nestly</h2>
            <p className="mt-1 text-sm text-pink-100">Sign in by entering information below</p>

            <form onSubmit={onSubmit} noValidate className="mt-8 flex flex-col gap-4">
              {error ? (
                <div role="alert" className="rounded-xl bg-white/15 px-4 py-2 text-sm text-white">
                  {error}
                </div>
              ) : null}

              <div className="flex flex-col gap-1.5 text-left">
                <label htmlFor="email" className="text-sm font-semibold text-white">
                  Email *
                </label>
                <input
                  id="email"
                  type="email"
                  autoComplete="email"
                  placeholder="demo@example.com"
                  aria-invalid={form.formState.errors.email ? true : undefined}
                  {...form.register("email")}
                  className="rounded-xl border-none bg-white px-4 py-3.5 text-sm text-neutral-900 placeholder:text-neutral-400 outline-none focus:ring-2 focus:ring-white"
                />
                {form.formState.errors.email ? (
                  <p className="text-xs text-pink-100">{form.formState.errors.email.message}</p>
                ) : null}
              </div>

              <div className="flex flex-col gap-1.5 text-left">
                <label htmlFor="password" className="text-sm font-semibold text-white">
                  Password *
                </label>
                <input
                  id="password"
                  type="password"
                  autoComplete="current-password"
                  placeholder="••••••"
                  aria-invalid={form.formState.errors.password ? true : undefined}
                  {...form.register("password")}
                  className="rounded-xl border-none bg-white px-4 py-3.5 text-sm text-neutral-900 placeholder:text-neutral-400 outline-none focus:ring-2 focus:ring-white"
                />
                {form.formState.errors.password ? (
                  <p className="text-xs text-pink-100">{form.formState.errors.password.message}</p>
                ) : null}
              </div>

              <label className="flex items-center gap-2 text-sm text-white">
                <input
                  type="checkbox"
                  checked={remember}
                  onChange={(event) => setRemember(event.target.checked)}
                  className="h-4 w-4 rounded border-white/40 text-pink-600 focus:ring-white"
                />
                Remember my preference
              </label>

              <button
                type="submit"
                disabled={form.formState.isSubmitting}
                className="mt-2 rounded-xl bg-white px-5 py-3.5 text-sm font-semibold text-pink-600 shadow-md transition hover:bg-pink-50 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {form.formState.isSubmitting ? "Signing in…" : "Sign In"}
              </button>
            </form>
          </div>
        </section>
      </div>
    </main>
  );
}
