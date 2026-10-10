"use client";

import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import type { ReactNode } from "react";
import { AdminHeader } from "@/components/AdminHeader";
import { AdminSidebar } from "@/components/AdminSidebar";
import { RequireAdminAuth } from "@/components/RequireAdminAuth";
import { getSessionClaims, subscribeToAuthChanges } from "@/lib/auth";
import type { AdminSessionClaims } from "@/lib/types";

/**
 * Authenticated app shell (task 98b): header + sidebar + content area, shown
 * once signed in. Every route nested under the `(admin)` group (this route
 * group segment does not appear in the URL) automatically gets this chrome
 * and the RequireAdminAuth guard - later tasks that add a module's page only
 * need to drop `src/app/(admin)/<module>/page.tsx` in place.
 *
 * The sidebar is permanent from `lg` up and becomes a drawer below it: at
 * 20 modules a fixed 240px rail left no usable content width on a laptop,
 * let alone a tablet.
 */
export default function AuthenticatedLayout({ children }: { children: ReactNode }) {
  const [claims, setClaims] = useState<AdminSessionClaims | null>(null);
  const [navOpen, setNavOpen] = useState(false);
  const pathname = usePathname();

  useEffect(() => {
    const sync = () => setClaims(getSessionClaims());
    sync();
    return subscribeToAuthChanges(sync);
  }, []);

  // Reaching the destination should dismiss the drawer, not leave it covering
  // the page that was just navigated to. Adjusting state when a prop changes
  // (react.dev/learn/you-might-not-need-an-effect), not an effect.
  const [navOpenForPathname, setNavOpenForPathname] = useState(pathname);
  if (pathname !== navOpenForPathname) {
    setNavOpenForPathname(pathname);
    setNavOpen(false);
  }

  useEffect(() => {
    if (!navOpen) return;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") setNavOpen(false);
    };
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("keydown", onKeyDown);
      document.body.style.overflow = previousOverflow;
    };
  }, [navOpen]);

  return (
    <RequireAdminAuth>
      <div className="flex min-h-screen bg-bg">
        <div className="sticky top-0 hidden h-screen lg:flex">
          <AdminSidebar claims={claims} />
        </div>

        {navOpen ? (
          <div className="fixed inset-0 z-50 lg:hidden">
            <div
              className="absolute inset-0 animate-fade-in bg-overlay/50 backdrop-blur-[2px]"
              onClick={() => setNavOpen(false)}
              aria-hidden
            />
            <div className="absolute inset-y-0 left-0 flex animate-rise">
              <AdminSidebar claims={claims} onNavigate={() => setNavOpen(false)} />
            </div>
          </div>
        ) : null}

        {/* bg-surface (white): matches the MatDash reference's
            `body-wrapper` class (`bg-white dark:bg-darkgray`) exactly - this
            column is white behind the header; the tinted canvas is `<main>`
            below, not this wrapper. */}
        <div className="flex min-w-0 flex-1 flex-col bg-surface">
          <AdminHeader claims={claims} onOpenNav={() => setNavOpen(true)} />

          {/* bg-bg: the MatDash reference nests a tinted scroll canvas
              (computed rgb(244,247,251), i.e. this app's --bg) directly
              behind the page's cards, inside the white body-wrapper column -
              that's what makes a borderless white Card read as a distinct
              surface instead of blending into the page. Padding matched to
              the reference's `.container` (computed 30px on every side at
              desktop; smaller below `lg`, where 30px is a lot to give up). */}
          <main className="min-w-0 flex-1 bg-bg px-4 py-6 sm:px-6 lg:px-[30px] lg:py-[30px]">
            <div className="mx-auto w-full max-w-7xl">{children}</div>
          </main>
        </div>
      </div>
    </RequireAdminAuth>
  );
}
