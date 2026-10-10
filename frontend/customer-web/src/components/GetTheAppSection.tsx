"use client";

import { motion } from "motion/react";
import { QRCodeSVG } from "qrcode.react";
import { useSyncExternalStore } from "react";
import type { ReactNode } from "react";
import { Reveal, revealItem, SPRING } from "@/components/motion";
import { LinkButton } from "@/components/ui";
import { PROVIDER_WEB_URL } from "@/lib/unified-login-api";

/**
 * "Get Glavyx on your phone": the two separate installable apps (customer and
 * provider - each is its own PWA with its own manifest and `/install-app`
 * page) surfaced on the home page, instead of only the small footer "Get the
 * App" link. Both buttons land on an install page that adapts to the visitor's
 * device (one-tap install on Android, Add-to-Home-Screen steps on iOS), so
 * nothing here needs to sniff the platform itself.
 *
 * Each card also shows a QR code on desktop-width screens: a visitor on a
 * laptop can't install a phone app from the browser they're in, but they can
 * point their phone camera at the code and land on the right install page.
 * Phones skip the code - they tap the button instead.
 *
 * There is deliberately no app-store badge: neither app is published to Google
 * Play or the App Store, and claiming otherwise would be a dead link.
 */
export function GetTheAppSection() {
  const origin = useOrigin();

  return (
    <section aria-labelledby="get-app-heading" className="flex flex-col gap-6">
      <div className="flex flex-col gap-1.5">
        <h2 id="get-app-heading" className="text-xl font-semibold tracking-tight text-fg">
          Get Glavyx on your phone
        </h2>
        <p className="text-sm text-fg-muted">
          Add it to your home screen in a few taps — it opens like any other app, with no app-store download.
        </p>
      </div>

      <Reveal className="grid grid-cols-1 gap-5 md:grid-cols-2">
        <motion.div
          variants={revealItem}
          whileHover={{ y: -4 }}
          transition={SPRING}
          className="flex flex-col gap-5 rounded-2xl border border-line bg-surface p-6 shadow-xs transition-shadow duration-200 ease-out hover:shadow-md"
        >
          <AppIcon tone="soft">
            <PhoneIcon />
          </AppIcon>
          <div className="flex flex-col gap-1.5">
            <h3 className="text-lg font-semibold tracking-tight text-fg">Glavyx for customers</h3>
            <p className="text-sm leading-relaxed text-fg-muted">
              Book trusted home services in a few taps, follow your professional on the way, and manage every
              booking from your home screen.
            </p>
          </div>
          <div className="mt-auto flex items-end justify-between gap-4">
            <LinkButton href="/install-app" size="lg">
              Install the app
            </LinkButton>
            {origin ? (
              <QrTile value={`${origin}/install-app`} title="Scan to install the Glavyx customer app" tone="soft" />
            ) : null}
          </div>
        </motion.div>

        <motion.div
          variants={revealItem}
          whileHover={{ y: -4 }}
          transition={SPRING}
          className="flex flex-col gap-5 rounded-2xl bg-brand-gradient p-6 text-fg-on-brand shadow-brand"
        >
          <AppIcon tone="onBrand">
            <ToolboxIcon />
          </AppIcon>
          <div className="flex flex-col gap-1.5">
            <h3 className="text-lg font-semibold tracking-tight">Glavyx for professionals</h3>
            <p className="text-sm leading-relaxed opacity-90">
              Receive job offers near you, set your own availability, and track every job and payout in one
              place. A separate app, built for the field.
            </p>
          </div>
          <div className="mt-auto flex items-end justify-between gap-4">
            <div className="flex flex-wrap items-center gap-x-5 gap-y-3">
              <LinkButton href={`${PROVIDER_WEB_URL}/install-app`} variant="secondary" size="lg">
                Install the provider app
              </LinkButton>
              <a
                href={`${PROVIDER_WEB_URL}/register`}
                className="text-sm font-medium underline underline-offset-4 hover:opacity-90"
              >
                Become a provider
              </a>
            </div>
            <QrTile
              value={`${PROVIDER_WEB_URL}/install-app`}
              title="Scan to install the Glavyx provider app"
              tone="onBrand"
            />
          </div>
        </motion.div>
      </Reveal>
    </section>
  );
}

/** The page's own origin, read on the client only - `""` during SSR/hydration, so the customer QR appears right after mount instead of mismatching the server HTML. */
function subscribeToNothing(): () => void {
  return () => undefined;
}

function useOrigin(): string {
  return useSyncExternalStore(
    subscribeToNothing,
    () => window.location.origin,
    () => "",
  );
}

/**
 * Desktop-width only (`md` and up): below that the visitor is already on a
 * phone and the card's button does the job. The tile is white in both themes
 * on purpose - a QR code needs dark-on-light contrast to scan, which
 * token-driven colours would invert in dark mode.
 */
function QrTile({ value, title, tone }: { value: string; title: string; tone: "soft" | "onBrand" }) {
  return (
    <div className="hidden shrink-0 flex-col items-center gap-1.5 md:flex">
      <div className="rounded-xl border border-line bg-white p-2.5 shadow-xs">
        <QRCodeSVG value={value} size={96} level="M" marginSize={0} title={title} />
      </div>
      <span className={tone === "onBrand" ? "text-xs font-medium opacity-90" : "text-xs font-medium text-fg-muted"}>
        Scan with your phone
      </span>
    </div>
  );
}

function AppIcon({ tone, children }: { tone: "soft" | "onBrand"; children: ReactNode }) {
  return (
    <span
      aria-hidden="true"
      className={
        tone === "soft"
          ? "flex h-12 w-12 items-center justify-center rounded-xl bg-brand-50 text-brand-600 dark:bg-brand-500/15 dark:text-brand-400"
          : "flex h-12 w-12 items-center justify-center rounded-xl bg-white/15 text-white"
      }
    >
      {children}
    </span>
  );
}

const ICON_PROPS = {
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: "1.75",
  strokeLinecap: "round",
  strokeLinejoin: "round",
  className: "h-6 w-6",
  "aria-hidden": true,
} as const;

function PhoneIcon() {
  return (
    <svg {...ICON_PROPS}>
      <rect x="7" y="2.5" width="10" height="19" rx="2.5" />
      <path d="M11 18.5h2" />
    </svg>
  );
}

function ToolboxIcon() {
  return (
    <svg {...ICON_PROPS}>
      <rect x="3" y="8" width="18" height="12" rx="2" />
      <path d="M9 8V6a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v2M3 13h18M11 13v2h2v-2" />
    </svg>
  );
}
