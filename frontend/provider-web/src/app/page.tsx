"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { isAuthenticated } from "@/lib/auth";

/**
 * The provider portal has no public landing page - send the visitor straight
 * to the jobs list (if a live session exists, the day-to-day provider
 * screen) or the login screen otherwise. Mirrors admin-web's root page.
 */
export default function RootPage() {
  const router = useRouter();

  useEffect(() => {
    router.replace(isAuthenticated() ? "/jobs" : "/login");
  }, [router]);

  return <p className="p-8 text-sm text-neutral-500">Loading…</p>;
}
