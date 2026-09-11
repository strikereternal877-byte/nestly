"use client";

import { useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { QRCodeSVG } from "qrcode.react";
import { AuthShell } from "@/components/auth-ui";
import { Button } from "@/components/ui";

/** Set once this screen has been shown, so a returning sign-in doesn't nag every time - only a fresh registration always shows it. */
const SEEN_KEY = "nestly.provider.install-prompt.seen";

/** Same open-redirect guard as customer-web's return-to.ts: only a same-site path is ever followed. */
function resolveNext(value: string | null): string | null {
  if (value === null) return null;
  if (!value.startsWith("/") || value.startsWith("//") || value.startsWith("/\\")) return "/today";
  return value;
}

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
 * direct visit via the "Get the app" link on every auth screen (no `next`,
 * this app's only reachable surface for a signed-out visitor). The former
 * keeps the original behaviour - skip straight past on
 * desktop/already-installed/already-seen, forward to `next` once done here -
 * since forcing a stop mid sign-in would strand anyone who dismissed this
 * before. The latter never auto-navigates away: a visitor who came here on
 * purpose should see something useful regardless of device, not get
 * silently bounced back where they came from.
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
  const next = resolveNext(useSearchParams().get("next"));
  const [platform, setPlatform] = useState<Platform | null>(null);
  const [standalone, setStandalone] = useState(false);
  const [deferredPrompt, setDeferredPrompt] = useState<BeforeInstallPromptEvent | null>(null);
  const [installed, setInstalled] = useState(false);

  useEffect(() => {
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
            {next !== null ? "Continue" : "Done"}
          </Button>
        </div>
      </AuthShell>
    );
  }

  if (platform === "desktop") {
    // Row 68 in docs/OPEN-FIXES-FEATURES.csv: a text-only "open this on your
    // phone" message is a dead end for a desktop visitor with no other way
    // forward. The install steps themselves are a genuine technical
    // blocker on desktop (there's no home-screen/PWA install surface for a
    // desktop browser here), so the fix is a scannable QR code straight to
    // this same page's mobile install flow - not a "continue anyway" link,
    // since there is nothing on desktop for that link to unlock.
    const installUrl =
      typeof window !== "undefined" ? `${window.location.origin}/install-app` : "/install-app";

    return (
      <AuthShell
        title="Get Glavyx Provider on your phone"
        subtitle="The provider portal installs like an app from your phone's browser — this page can't install it here."
      >
        <div className="flex flex-col items-center gap-4">
          {/* Deliberately literal white/black rather than the surface/fg
              tokens: a QR code's scanability depends on real light-on-dark
              contrast, which must hold in dark mode too - token-driven
              colors here would invert to a low-contrast dark-on-dark code. */}
          <div className="rounded-2xl border border-line bg-white p-4">
            <QRCodeSVG value={installUrl} size={168} level="M" marginSize={0} title="Scan to open the install page on your phone" />
          </div>
          <p className="rounded-xl border border-line bg-surface-subtle px-4 py-3 text-center text-sm font-medium text-fg">
            Scan this code with your phone&apos;s camera, or open this page on your phone to continue.
          </p>
        </div>
      </AuthShell>
    );
  }

  return (
    <AuthShell
      title="Add Glavyx Provider to your home screen"
      subtitle="One tap, and the provider portal opens like any other app — faster, full-screen, and easy to find between jobs."
    >
      <div className="flex flex-col gap-6">
        {platform === "android" && deferredPrompt ? (
          <Button size="lg" fullWidth onClick={install}>
            Install Glavyx Provider
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
  "Confirm, and the provider portal appears on your home screen.",
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
