"use client";

import type { ReactNode } from "react";

/**
 * Shared frame for admin-web's authentication screens.
 *
 * A deliberately trimmed sibling of customer-web/src/components/auth-ui.tsx:
 * the admin app authenticates with email and password only, so the OTP field,
 * resend countdown and account-type segmented control that file carries would
 * be dead code here.
 *
 * Row 69 in docs/OPEN-FIXES-FEATURES.csv: unlike customer-web/provider-web
 * (consumer-facing, mobile-first), admin-web is the desk-first ops console
 * (see docs/FRONTEND.md) - every other screen in this app assumes real
 * desktop width, so a narrow centered mobile-width card here read as a dead
 * viewport either side of it. From `lg:` up this now splits into a branded
 * panel plus the form, using the freed width instead of framing it in
 * emptiness; below `lg:` it collapses back to the original single centered
 * card, unchanged.
 */
export function AuthShell({
  title,
  subtitle,
  children,
  footer,
}: {
  title: string;
  subtitle?: string;
  children: ReactNode;
  footer?: ReactNode;
}) {
  return (
    <main className="relative isolate flex min-h-screen flex-col overflow-hidden lg:flex-row">
      {/* Branded side panel - desktop only. Carries the wordmark so the form
          panel's own heading can stay focused on the task at hand. */}
      <div className="relative isolate hidden shrink-0 flex-col justify-between overflow-hidden bg-brand-gradient px-12 py-12 text-fg-on-brand lg:flex lg:w-[38%] xl:w-[34%]">
        <div
          aria-hidden
          className="absolute -bottom-24 -left-24 h-80 w-80 rounded-full bg-white/10 blur-3xl"
        />
        <div
          aria-hidden
          className="absolute -right-16 -top-16 h-64 w-64 rounded-full bg-white/10 blur-3xl"
        />

        <span className="relative inline-flex items-center gap-2.5">
          <span
            aria-hidden
            className="flex h-10 w-10 items-center justify-center rounded-xl bg-white/15 backdrop-blur-sm"
          >
            <svg viewBox="0 0 24 24" fill="none" className="h-5 w-5">
              <path
                d="M4 11.5 12 5l8 6.5V19a1 1 0 0 1-1 1h-4v-5h-6v5H5a1 1 0 0 1-1-1v-7.5Z"
                fill="currentColor"
              />
            </svg>
          </span>
          <span className="text-lg font-semibold tracking-tight">
            Glavyx <span className="opacity-80">Admin</span>
          </span>
        </span>

        <div className="relative max-w-sm">
          <p className="text-2xl font-semibold leading-snug text-pretty">
            The operations console behind every booking, payout and provider.
          </p>
          <p className="mt-3 text-sm leading-relaxed text-white/75 text-pretty">
            Sign in to manage catalog, fulfilment, payments and support from one place.
          </p>
        </div>

        <p className="relative text-xs text-white/60">
          Authorised personnel only. Activity on this panel is audited.
        </p>
      </div>

      {/* Form panel. */}
      <div className="relative isolate flex flex-1 items-center justify-center overflow-hidden px-4 py-12">
        {/* Decorative brand wash - only needed once the branded panel above
            isn't already carrying the color, i.e. below `lg:`. */}
        <div
          aria-hidden
          className="absolute -top-40 left-1/2 -z-10 h-[28rem] w-[28rem] -translate-x-1/2 rounded-full bg-brand-500/10 blur-3xl lg:hidden"
        />

        <div className="w-full max-w-md animate-rise">
          <div className="mb-8 text-center lg:text-left">
            <span className="inline-flex items-center gap-2 lg:hidden">
              <span
                aria-hidden
                className="flex h-9 w-9 items-center justify-center rounded-xl bg-brand-gradient text-fg-on-brand shadow-brand"
              >
                <svg viewBox="0 0 24 24" fill="none" className="h-5 w-5">
                  <path
                    d="M4 11.5 12 5l8 6.5V19a1 1 0 0 1-1 1h-4v-5h-6v5H5a1 1 0 0 1-1-1v-7.5Z"
                    fill="currentColor"
                  />
                </svg>
              </span>
              <span className="text-base font-semibold tracking-tight text-fg">
                Glavyx <span className="text-fg-muted">Admin</span>
              </span>
            </span>

            <h1 className="mt-6 text-display-sm font-semibold text-fg lg:mt-0">{title}</h1>
            {subtitle ? (
              <p className="mt-2 text-sm leading-relaxed text-fg-muted text-pretty">{subtitle}</p>
            ) : null}
          </div>

          <div className="rounded-2xl bg-surface p-6 shadow-md sm:p-7">
            {children}
          </div>

          {footer ? (
            <p className="mt-6 text-center text-sm text-fg-muted lg:text-left">{footer}</p>
          ) : null}
        </div>
      </div>
    </main>
  );
}
