"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import type { ReactNode } from "react";
import { CitySelector } from "@/components/CitySelector";
import { ThemeToggle } from "@/components/ThemeToggle";
import { Button, LinkButton, cx } from "@/components/ui";
import { API_V1, apiFetch } from "@/lib/api";
import {
  clearSession,
  getRefreshToken,
  isAuthenticated,
  subscribeToAuthChanges,
} from "@/lib/auth";
import {
  DevicePlatform,
  registerDeviceToken,
  revokeDeviceToken,
  storeDeviceTokenId,
  takeDeviceTokenId,
} from "@/lib/device-tokens-api";
import { requestPushToken } from "@/lib/push";
import { useFeatureFlags, type CustomerFeatureFlags } from "@/lib/feature-flags";

/**
 * Global chrome for customer-web.
 *
 * The previous header put all nine signed-in destinations in one flat row,
 * which wrapped into an unreadable block on a laptop and was unusable on a
 * phone. Only the genuinely navigational links stay in the bar; the rest are
 * account destinations and now sit behind an account menu, with the same set
 * mirrored into a drawer at mobile widths.
 *
 * "Editorial" chrome v6: on the home route, before the page scrolls, the bar
 * is transparent and floats directly over `HeroBanner`'s photo — white text
 * with the same text-shadow treatment the hero's own copy uses, so the two
 * sections read as one continuous piece rather than a chrome bar stacked on
 * top of a banner. Past a small scroll threshold (`SCROLL_SOLID_PX`), or on
 * every other route, it's the solid bar this file already had: `bg-surface`,
 * hairline bottom rule, underline-indicator nav links (same active-tab idiom
 * as `Tabs` in ui.tsx).
 *
 * The bar is *permanently* `fixed` rather than switching between `fixed`
 * (over the hero) and `sticky` (everywhere else) — only its colors ever
 * transition. Toggling position type at the exact moment `scrolled` flips
 * would re-flow the header into document flow mid-scroll and visibly jump
 * the page; toggling only `bg-*`/`text-*`/`border-*` classes can't. Every
 * route compensates via `#main`'s `pt-[4.5rem]` in `app/layout.tsx`; the
 * home hero cancels that one padding back out (`-mt-[4.5rem]` in
 * `HeroBanner.tsx`) so it still starts at true y=0 under the transparent bar.
 */
const SCROLL_SOLID_PX = 24;

/**
 * Account-menu destinations, in the order they matter to a signed-in
 * customer. Exported (not just used here) so app/profile/page.tsx's "Quick
 * links" sidebar card can reuse the exact same list rather than keeping a
 * second copy that could drift from this one.
 */
export const ACCOUNT_LINKS: readonly {
  href: string;
  label: string;
  flagKey?: keyof CustomerFeatureFlags;
}[] = [
  { href: "/bookings", label: "My bookings" },
  { href: "/recurring-bookings", label: "Recurring bookings" },
  { href: "/addresses", label: "Addresses" },
  { href: "/wallet", label: "Wallet", flagKey: "walletEnabled" },
  { href: "/subscription", label: "Glavyx Plus" },
  { href: "/amc", label: "AMC Plans", flagKey: "amcSubscriptionsEnabled" },
  { href: "/refer-earn", label: "Refer & Earn", flagKey: "referralsEnabled" },
  { href: "/support", label: "Support" },
  { href: "/profile", label: "Profile" },
];

/**
 * `ACCOUNT_LINKS` filtered by each entry's optional `flagKey` (SRS 12.19
 * "Feature flags") - shared by every consumer of the list (`AccountMenu`,
 * `MobileDrawer`, and `patterns.tsx`'s `AccountQuickLinksCard`) so a disabled
 * feature disappears from all three at once rather than needing the same
 * filter re-applied at each call site.
 */
export function useVisibleAccountLinks() {
  const flags = useFeatureFlags();
  return ACCOUNT_LINKS.filter((link) => !link.flagKey || flags[link.flagKey]);
}

export function SiteHeader() {
  const router = useRouter();
  const pathname = usePathname();
  const [authed, setAuthed] = useState(false);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const isHome = pathname === "/";
  const [scrolled, setScrolled] = useState(false);

  useEffect(() => {
    const sync = () => setAuthed(isAuthenticated());
    sync();
    return subscribeToAuthChanges(sync);
  }, []);

  // Booking/assignment status notifications (task 307's customer-side
  // counterpart - provider-web has the identical effect in its
  // (provider)/layout.tsx). Runs whenever `authed` flips to true - once at
  // load for a returning signed-in customer, once more per fresh sign-in -
  // rather than on every render, since SiteHeader itself never remounts
  // across navigation (it lives in the root layout). No-ops entirely when
  // Firebase is not configured or the browser declines - see lib/push.ts.
  useEffect(() => {
    if (!authed) return;
    let cancelled = false;

    void (async () => {
      const token = await requestPushToken();
      if (cancelled || !token) return;

      try {
        const registered = await registerDeviceToken(DevicePlatform.Fcm, token);
        if (!cancelled) storeDeviceTokenId(registered.id);
      } catch {
        // Best-effort: a customer with no working push still has a fully
        // working account. Nothing here blocks any other part of the app.
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [authed]);

  // A route change means the destination was reached — leaving the drawer open
  // over the new page is never what the customer wanted. "Adjusting state
  // when a prop changes" (react.dev/learn/you-might-not-need-an-effect),
  // during render rather than in an effect, tracking pathname itself as the
  // comparison so this only fires on an actual route change.
  const [drawerClosedForPathname, setDrawerClosedForPathname] = useState(pathname);
  if (pathname !== drawerClosedForPathname) {
    setDrawerClosedForPathname(pathname);
    setDrawerOpen(false);
  }

  // Only the home route ever needs this listener (elsewhere `transparent` is
  // always false), but mounting it unconditionally keeps the hook order
  // stable across route changes rather than conditionally calling useEffect.
  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > SCROLL_SOLID_PX);
    onScroll();
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => window.removeEventListener("scroll", onScroll);
  }, []);

  const transparent = isHome && !scrolled;

  const signOut = async () => {
    const refreshToken = getRefreshToken();
    const deviceTokenId = takeDeviceTokenId();

    // Clear locally regardless of the server's answer: the customer asked to
    // be signed out, and a network failure must not leave the token behind.
    try {
      if (refreshToken) {
        await apiFetch(`${API_V1}/auth/logout`, {
          method: "POST",
          body: JSON.stringify({ refreshToken }),
        });
      }

      // Best-effort, same as the token registration itself: a signed-out
      // device should stop receiving booking/assignment push notifications,
      // but that must never block sign-out.
      if (deviceTokenId) {
        await revokeDeviceToken(deviceTokenId);
      }
    } catch {
      // Already-invalid tokens are a no-op server-side; nothing to report.
    } finally {
      clearSession();
      router.push("/login");
    }
  };

  return (
    <header
      className={cx(
        // Task #351: permanently `fixed` at the true top edge, and this app
        // installs as a standalone PWA (manifest.json's `display:
        // "standalone"`) - on a notched/punch-hole phone in that mode there
        // is no browser chrome above it, so the bar itself must clear the
        // notch. `pt-[env(safe-area-inset-top)]` grows the header by the
        // inset instead of the nav row shrinking under it; `#main`'s
        // `pt-[4.5rem]`, `HeroBanner`'s cancelling `-mt-[4.5rem]`, and
        // `OfflineBanner`'s `top-[4.5rem]` all account for the same grown
        // height so nothing sits under (or is clipped by) the taller bar.
        "fixed inset-x-0 top-0 z-40 pt-[env(safe-area-inset-top)] transition-[background-color,border-color,backdrop-filter] duration-300",
        transparent
          ? "border-b border-transparent bg-transparent"
          : "border-b border-line/70 bg-surface/85 backdrop-blur-md",
      )}
    >
      <nav
        aria-label="Primary"
        className="mx-auto flex h-[4.5rem] w-full max-w-7xl items-center gap-3 px-4 sm:px-6"
      >
        <Link href="/" className="flex shrink-0 items-center gap-2" aria-label="Glavyx home">
          <GlavyxMark />
          <span
            style={transparent ? TEXT_SHADOW : undefined}
            className={cx(
              "text-base font-bold tracking-tight transition-colors duration-300",
              transparent ? "text-white" : "text-fg",
            )}
          >
            Glavyx
          </span>
        </Link>

        <span
          aria-hidden
          className={cx(
            "mx-5 hidden h-6 w-px transition-colors duration-300 md:block",
            transparent ? "bg-white/30" : "bg-line",
          )}
        />

        <div className="hidden h-full items-center gap-6 md:flex">
          <NavLink href="/categories" active={pathname.startsWith("/categories")} transparent={transparent}>
            Categories
          </NavLink>
          <NavLink href="/search" active={pathname.startsWith("/search")} transparent={transparent}>
            Search
          </NavLink>
        </div>

        <div className="flex-1" />

        <div className="hidden items-center gap-2 md:flex">
          <CitySelector transparent={transparent} />
          <ThemeToggle
            className={transparent ? "!text-white hover:!bg-white/15 hover:!text-white" : undefined}
          />
          {authed ? (
            <AccountMenu onSignOut={signOut} pathname={pathname} transparent={transparent} />
          ) : (
            <>
              <Button
                variant="ghost"
                size="sm"
                onClick={() => router.push("/login")}
                className={transparent ? "!text-white hover:!bg-white/15 hover:!text-white" : undefined}
              >
                Sign in
              </Button>
              <Button size="sm" onClick={() => router.push("/register")}>
                Create account
              </Button>
            </>
          )}
        </div>

        <div className="flex items-center gap-1 md:hidden">
          <ThemeToggle
            className={transparent ? "!text-white hover:!bg-white/15 hover:!text-white" : undefined}
          />
          <button
            type="button"
            onClick={() => setDrawerOpen(true)}
            aria-label="Open menu"
            aria-expanded={drawerOpen}
            className={cx(
              "inline-flex h-9 w-9 items-center justify-center rounded-lg transition-colors duration-fast ease-out",
              transparent
                ? "text-white hover:bg-white/15 hover:text-white"
                : "text-fg-muted hover:bg-surface-3 hover:text-fg",
            )}
          >
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              className="h-5 w-5"
              aria-hidden
            >
              <path d="M4 7h16M4 12h16M4 17h16" />
            </svg>
          </button>
        </div>
      </nav>

      {drawerOpen ? (
        <MobileDrawer
          authed={authed}
          pathname={pathname}
          onClose={() => setDrawerOpen(false)}
          onSignOut={signOut}
        />
      ) : null}
    </header>
  );
}

/** Shared with `HeroBanner`'s own overlaid copy, so nav and banner text read as the same treatment. */
const TEXT_SHADOW = { textShadow: "0 1px 3px rgb(0 0 0 / 0.5), 0 4px 20px rgb(0 0 0 / 0.5)" };

/**
 * Glavyx wordmark glyph: a solid abstract "G" - a filled disc with a wedge
 * cut from its right edge, plugged by a stubby crossbar - rather than a
 * literal house/nest shape (that read was specific to "Nestly") or a
 * thin-stroked ring (which turned into an illegible smudge at real header/
 * favicon sizes - a solid fill holds its shape under anti-aliasing at those
 * sizes where a 2-3px stroke does not). Shared 1:1 with `icon.svg` (the
 * PWA/app icon source) and `auth-ui.tsx`'s standalone login/register logo -
 * all three must be edited together or the brand mark drifts across
 * surfaces.
 */
function GlavyxMark() {
  return (
    <span
      aria-hidden
      className="flex h-10 w-10 items-center justify-center rounded-xl bg-brand-600 text-fg-on-brand shadow-brand"
    >
      <svg viewBox="0 0 24 24" fill="none" className="h-6 w-6">
        <circle
          cx="12"
          cy="12"
          r="9"
          stroke="currentColor"
          strokeWidth="4.5"
          strokeLinecap="round"
          strokeDasharray="44 13"
          transform="rotate(40 12 12)"
        />
        <line x1="13.5" y1="12" x2="21" y2="12" stroke="currentColor" strokeWidth="4.5" strokeLinecap="round" />
      </svg>
    </span>
  );
}

function NavLink({
  href,
  active,
  transparent,
  children,
}: {
  href: string;
  active: boolean;
  transparent: boolean;
  children: ReactNode;
}) {
  return (
    <Link
      href={href}
      aria-current={active ? "page" : undefined}
      style={transparent ? TEXT_SHADOW : undefined}
      className={cx(
        "relative flex h-full items-center text-sm font-medium transition-colors duration-fast ease-out",
        transparent
          ? active
            ? "text-white"
            : "text-white/80 hover:text-white"
          : active
            ? "text-brand-600 dark:text-brand-400"
            : "text-fg-muted hover:text-fg",
      )}
    >
      {children}
      {active ? (
        // Same indicator idiom as `Tabs` in ui.tsx — sits on the header's
        // own bottom rule so it reads as part of it, not a floating bar.
        <span
          className={cx(
            "absolute inset-x-0 -bottom-px h-0.5 rounded-full",
            transparent ? "bg-white" : "bg-brand-600 dark:bg-brand-400",
          )}
        />
      ) : null}
    </Link>
  );
}

/** Signed-in account dropdown. Closes on Escape, outside click, and route change. */
function AccountMenu({
  onSignOut,
  pathname,
  transparent,
}: {
  onSignOut: () => void;
  pathname: string;
  transparent: boolean;
}) {
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const accountLinks = useVisibleAccountLinks();

  // "Adjusting state when a prop changes", during render rather than in an
  // effect - see the drawer-close above for the same pattern.
  const [closedForPathname, setClosedForPathname] = useState(pathname);
  if (pathname !== closedForPathname) {
    setClosedForPathname(pathname);
    setOpen(false);
  }

  useEffect(() => {
    if (!open) return;

    const onPointerDown = (event: MouseEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") setOpen(false);
    };

    document.addEventListener("mousedown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("mousedown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  return (
    <div ref={containerRef} className="relative">
      <button
        type="button"
        onClick={() => setOpen((current) => !current)}
        aria-haspopup="menu"
        aria-expanded={open}
        style={transparent ? TEXT_SHADOW : undefined}
        className={cx(
          "inline-flex h-9 items-center gap-2 rounded-lg px-2.5 text-sm font-medium transition-colors duration-fast ease-out",
          transparent
            ? "border border-white/30 bg-white/10 text-white hover:bg-white/20"
            : "border border-line bg-surface text-fg shadow-xs hover:border-line-strong hover:bg-surface-2",
        )}
      >
        <span className="flex h-6 w-6 items-center justify-center rounded-full bg-brand-600 text-fg-on-brand">
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            className="h-3.5 w-3.5"
            aria-hidden
          >
            <circle cx="12" cy="8" r="3.5" />
            <path d="M5 20a7 7 0 0 1 14 0" strokeLinecap="round" />
          </svg>
        </span>
        <span className="hidden lg:inline">Account</span>
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          className={cx(
            "h-3.5 w-3.5 transition-transform duration-fast",
            transparent ? "text-white/80" : "text-fg-subtle",
            open && "rotate-180",
          )}
          aria-hidden
        >
          <path d="m6 9 6 6 6-6" />
        </svg>
      </button>

      {open ? (
        <div
          role="menu"
          className="absolute right-0 top-full z-50 mt-2 w-56 animate-pop overflow-hidden rounded-xl border border-line bg-surface p-1.5 shadow-lg"
        >
          {accountLinks.map((link) => (
            <Link
              key={link.href}
              href={link.href}
              role="menuitem"
              className={cx(
                "block rounded-lg px-3 py-2 text-sm transition-colors duration-fast ease-out",
                pathname.startsWith(link.href)
                  ? "bg-surface-3 font-medium text-fg"
                  : "text-fg-muted hover:bg-surface-2 hover:text-fg",
              )}
            >
              {link.label}
            </Link>
          ))}
          <div className="my-1.5 border-t border-line" />
          <button
            type="button"
            role="menuitem"
            onClick={onSignOut}
            className="block w-full rounded-lg px-3 py-2 text-left text-sm text-danger transition-colors duration-fast ease-out hover:bg-danger-soft"
          >
            Sign out
          </button>
        </div>
      ) : null}
    </div>
  );
}

/** Full-height slide-over for mobile widths, holding the same destinations. */
function MobileDrawer({
  authed,
  pathname,
  onClose,
  onSignOut,
}: {
  authed: boolean;
  pathname: string;
  onClose: () => void;
  onSignOut: () => void;
}) {
  const panelRef = useRef<HTMLDivElement>(null);
  const accountLinks = useVisibleAccountLinks();

  // This is a hand-rolled dialog rather than the shared `Modal` (a right-side
  // slide-over, not `Modal`'s centered/bottom-sheet shape), so it has to
  // reimplement the same two behaviours `Modal` gives every other dialog in
  // the product: focus can't escape past the drawer via Tab while it's the
  // overlay content, and closing it must hand focus back to whatever opened
  // it rather than dropping it on `<body>`.
  useEffect(() => {
    const previouslyFocused = document.activeElement as HTMLElement | null;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    panelRef.current?.focus();

    const focusables = () =>
      Array.from(
        panelRef.current?.querySelectorAll<HTMLElement>(
          'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
        ) ?? [],
      );

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        onClose();
        return;
      }
      if (event.key !== "Tab") return;

      const items = focusables();
      if (items.length === 0) return;

      const first = items[0];
      const last = items[items.length - 1];
      const active = document.activeElement;

      if (event.shiftKey && (active === first || !panelRef.current?.contains(active))) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && active === last) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener("keydown", onKeyDown, true);

    return () => {
      document.removeEventListener("keydown", onKeyDown, true);
      document.body.style.overflow = previousOverflow;
      previouslyFocused?.focus();
    };
  }, [onClose]);

  // Portalled to <body> rather than rendered in place: `fixed inset-0` only
  // covers the actual viewport if every ancestor is un-filtered/untransformed
  // - a `backdrop-filter`/`filter`/`transform` anywhere up the tree (e.g.
  // this very drawer's parent, SiteHeader, once its `backdrop-blur-md` kicks
  // in past the scroll threshold) makes that ancestor the containing block
  // instead, squashing the drawer into SiteHeader's own 72px-tall box rather
  // than the viewport - exactly the bug CitySelector's modal had (see Modal's
  // own doc comment in ui.tsx) before it was portalled. Same fix here.
  return createPortal(
    <div className="fixed inset-0 z-50 md:hidden">
      <div
        className="absolute inset-0 animate-fade-in bg-overlay/50 backdrop-blur-[2px]"
        onClick={onClose}
        aria-hidden
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label="Menu"
        tabIndex={-1}
        // Task #351: `inset-y-0` spans the full physical viewport height, so
        // both the close button in the `shrink-0` header row below and the
        // sign-in/out CTA in the `shrink-0` footer row are genuinely at the
        // top/bottom screen edges - not inside the scrollable middle
        // section, which is the case the audit's guidance says to leave
        // alone. Padding on this outer flex container (rather than on the
        // fixed-height `h-16` header row) lets the flex column absorb the
        // extra space instead of squeezing that row's own height.
        className="absolute inset-y-0 right-0 flex w-[min(20rem,85vw)] animate-rise flex-col border-l border-line bg-surface pt-[env(safe-area-inset-top)] pb-[env(safe-area-inset-bottom)] outline-none"
      >
        <div className="flex h-16 shrink-0 items-center justify-between border-b border-line px-4">
          <span className="text-sm font-semibold text-fg">Menu</span>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close menu"
            className="inline-flex h-9 w-9 items-center justify-center rounded-lg text-fg-muted hover:bg-surface-3 hover:text-fg"
          >
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              className="h-5 w-5"
              aria-hidden
            >
              <path d="M18 6 6 18M6 6l12 12" />
            </svg>
          </button>
        </div>

        <div className="flex-1 overflow-y-auto p-4">
          <div className="mb-4">
            <CitySelector />
          </div>

          <DrawerLink href="/categories" pathname={pathname}>
            Categories
          </DrawerLink>
          <DrawerLink href="/search" pathname={pathname}>
            Search
          </DrawerLink>

          {authed ? (
            <>
              <p className="mb-1 mt-5 px-3 text-xs font-semibold uppercase tracking-wide text-fg-subtle">
                Account
              </p>
              {accountLinks.map((link) => (
                <DrawerLink key={link.href} href={link.href} pathname={pathname}>
                  {link.label}
                </DrawerLink>
              ))}
            </>
          ) : null}
        </div>

        <div className="shrink-0 border-t border-line p-4">
          {authed ? (
            <Button variant="secondary" fullWidth onClick={onSignOut}>
              Sign out
            </Button>
          ) : (
            // Real links rather than Buttons with router.push: these are plain
            // navigations, so they must stay middle-clickable and openable in
            // a new tab.
            <div className="flex flex-col gap-2">
              <LinkButton href="/register" fullWidth>
                Create account
              </LinkButton>
              <LinkButton href="/login" variant="secondary" fullWidth>
                Sign in
              </LinkButton>
            </div>
          )}
        </div>
      </div>
    </div>,
    document.body,
  );
}

function DrawerLink({
  href,
  pathname,
  children,
}: {
  href: string;
  pathname: string;
  children: ReactNode;
}) {
  const active = pathname === href || pathname.startsWith(`${href}/`);
  return (
    <Link
      href={href}
      aria-current={active ? "page" : undefined}
      className={cx(
        "block rounded-lg px-3 py-2.5 text-sm transition-colors duration-fast ease-out",
        active
          ? "bg-surface-3 font-medium text-fg"
          : "text-fg-muted hover:bg-surface-2 hover:text-fg",
      )}
    >
      {children}
    </Link>
  );
}
