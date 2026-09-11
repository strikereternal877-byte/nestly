import Link from "next/link";

/**
 * Low-risk in-flow help affordance for the booking funnel (docs/OPEN-FIXES-FEATURES.csv
 * "Booking flow, Support entry point" - previously the only way to ask a
 * question before booking was the footer's generic Contact Us link, off the
 * primary flow entirely).
 *
 * Deliberately reuses the existing support-ticket flow (`/support/new`)
 * rather than standing up a new chat/ticket system - that's out of scope for
 * this fix, and a real, already-built entry point already exists.
 */
export function BookingHelpLink() {
  return (
    <Link
      href="/support/new"
      className="inline-flex items-center justify-center gap-1.5 self-center text-sm font-medium text-fg-muted transition-colors duration-fast ease-out hover:text-brand-600 dark:hover:text-brand-400"
    >
      <HelpIcon />
      Need help? Contact support
    </Link>
  );
}

function HelpIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.75"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-4 w-4 shrink-0"
      aria-hidden
    >
      <circle cx="12" cy="12" r="9" />
      <path d="M9.5 9a2.5 2.5 0 0 1 4.7-1.2c.5.9.2 1.7-.5 2.3-.8.7-1.2 1.1-1.2 2.1" />
      <path d="M12 17h.01" />
    </svg>
  );
}
