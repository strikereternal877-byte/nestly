"use client";

import { useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { AuthShell } from "@/components/auth-ui";
import { Button } from "@/components/ui";
import { resolvePostLoginPath } from "@/lib/return-to";

/** Set once this screen has been shown, so a returning sign-in doesn't nag every time - only a fresh registration always shows it. */
const SEEN_KEY = "nestly.install-prompt.seen";

type Platform = "ios" | "android" | "desktop";

type BeforeInstallPromptEvent = Event & {
  prompt: () => Promise<void>;
  userChoice: Promise<{ outcome: "accepted" | "dismissed" }>;
};

function detectPlatform(): Platform {
  if (typeof navigator === "undefined") return "desktop";
  const ua = navigator.userAgent;
  if (/iPhone|iPad|iPod/.test(ua)) return "ios";
  if (/Android/.test(ua)) return "android";
  return "desktop";
}

function isStandalone(): boolean {
  if (typeof window === "undefined") return false;
  return (
    window.matchMedia("(display-mode: standalone)").matches ||
    (window.navigator as Navigator & { standalone?: boolean }).standalone === true
  );
}

/**
 * Two ways to land here: mid sign-in/registration (a `next` destination is
 * present in the URL - see login/page.tsx and register/page.tsx), or a
 * direct visit via the SiteFooter "Get the App" link (no `next`, reachable
 * signed in or out, any time). The former keeps the original behaviour -
 * skip straight past on desktop/already-installed/already-seen, forward to
 * `next` once done here - since forcing a stop mid sign-in would strand
 * anyone who dismissed this before. The latter never auto-navigates away:
 * a visitor who came here on purpose should see something useful regardless
 * of device, not get silently bounced back where they came from.
 */
export default function InstallAppPage() {
  return (
    <Suspense
      fallback={
        <AuthShell title="Almost there" subtitle="Setting things up.">
          <div />
        </AuthShell>
      }
    >
      <InstallScreen />
    </Suspense>
  );
}

function InstallScreen() {
  const router = useRouter();
  const nextParam = useSearchParams().get("next");
  const next = nextParam !== null ? resolvePostLoginPath(nextParam) : null;
  const [platform, setPlatform] = useState<Platform | null>(null);
  const [standalone, setStandalone] = useState(false);
  const [deferredPrompt, setDeferredPrompt] = useState<BeforeInstallPromptEvent | null>(null);
  const [installed, setInstalled] = useState(false);
  // Read from the live origin rather than hardcoded - glavyx.com isn't a
  // real, pointed-at domain yet, and this also keeps the message correct on
  // a Vercel preview URL or in local dev.
  const [origin, setOrigin] = useState("");

  useEffect(() => {
    // One-time mount bootstrap alongside the redirect decision and the
    // event listener below - not a continuously-synced external value, so
    // this deliberately stays an effect rather than a lazy initializer
    // (Next's SSR prerendering has no `window` to read from).
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setOrigin(window.location.origin);
    const detected = detectPlatform();
    const alreadySeen = window.localStorage.getItem(SEEN_KEY) === "1";
    const alreadyStandalone = isStandalone();

    // Auth-flow visit only: desktop, already installed, or this device has
    // seen the prompt before - nothing new to show, so don't nag, just
    // continue on to the real destination.
    if (next !== null && (detected === "desktop" || alreadyStandalone || alreadySeen)) {
      router.replace(next);
      return;
    }

    setPlatform(detected);
    setStandalone(alreadyStandalone);

    const onPrompt = (event: Event) => {
      event.preventDefault();
      setDeferredPrompt(event as BeforeInstallPromptEvent);
    };
    window.addEventListener("beforeinstallprompt", onPrompt);
    return () => window.removeEventListener("beforeinstallprompt", onPrompt);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [next]);

  const install = async () => {
    if (!deferredPrompt) return;
    await deferredPrompt.prompt();
    const { outcome } = await deferredPrompt.userChoice;
    setDeferredPrompt(null);
    if (outcome === "accepted") setInstalled(true);
  };

  const finish = () => {
    window.localStorage.setItem(SEEN_KEY, "1");
    if (next !== null) router.push(next);
  };

  if (platform === null) {
    // Detection + the auth-flow skip run synchronously in the effect above;
    // this frame only ever paints once there's actually something to show.
    return (
      <AuthShell title="Almost there" subtitle="Setting things up.">
        <div />
      </AuthShell>
    );
  }

  if (standalone || installed) {
    return (
      <AuthShell
        title="You're all set"
        subtitle="Open it any time straight from your home screen — no browser tabs, no typing the address again."
      >
        <div className="flex flex-col gap-6">
          <div className="flex items-center justify-center rounded-xl bg-success-soft py-8">
            <CheckIcon />
          </div>
          <Button size="lg" fullWidth onClick={finish}>
            {next !== null ? "Continue to Glavyx" : "Done"}
          </Button>
        </div>
      </AuthShell>
    );
  }

  if (platform === "desktop") {
    return (
      <AuthShell
        title="Get Glavyx on your phone"
        subtitle="Glavyx installs like an app from your phone's browser — this page can't install it here."
      >
        <p className="rounded-xl border border-line bg-surface-subtle px-4 py-3 text-center text-sm font-medium text-fg">
          Open {origin ? `${origin.replace(/^https?:\/\//, "")}/install-app` : "this page"} on your phone to
          continue.
        </p>
      </AuthShell>
    );
  }

  return (
    <AuthShell
      title="Add Glavyx to your home screen"
      subtitle="One tap, and Glavyx opens like any other app — faster, full-screen, and easy to find."
    >
      <div className="flex flex-col gap-6">
        {platform === "android" && deferredPrompt ? (
          <Button size="lg" fullWidth onClick={install}>
            Install Glavyx
          </Button>
        ) : (
          <ol className="flex flex-col gap-4">
            {(platform === "ios" ? IOS_STEPS : ANDROID_STEPS).map((step, index) => (
              <li key={step} className="flex items-start gap-3">
                <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-brand-gradient text-sm font-semibold text-fg-on-brand shadow-brand">
                  {index + 1}
                </span>
                <p className="pt-0.5 text-sm leading-relaxed text-fg">{step}</p>
              </li>
            ))}
          </ol>
        )}

        {next !== null ? (
          <Button size="lg" variant="ghost" fullWidth onClick={finish}>
            Maybe later
          </Button>
        ) : null}
      </div>
    </AuthShell>
  );
}

const IOS_STEPS = [
  "Tap the Share icon in Safari's toolbar.",
  'Scroll down and tap "Add to Home Screen".',
  'Tap "Add" in the top corner to confirm.',
];

const ANDROID_STEPS = [
  "Tap the menu (⋮) in the top corner of your browser.",
  'Tap "Install app" or "Add to Home screen".',
  "Confirm, and Glavyx appears on your home screen.",
];

function CheckIcon() {
  return (
    <svg viewBox="0 0 24 24" fill="none" className="h-10 w-10 text-success">
      <circle cx="12" cy="12" r="10" fill="currentColor" opacity="0.15" />
      <path
        d="M8 12.5l2.5 2.5L16 9"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}
