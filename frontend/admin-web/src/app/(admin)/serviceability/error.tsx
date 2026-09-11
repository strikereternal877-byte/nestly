"use client";

import { RouteErrorFallback } from "@/components/screen-states";

/**
 * Route-segment error boundary (task: premium UX audit, "Only one
 * root-level error boundary for the whole app"). Keeps the (admin) sidebar
 * and header mounted when this module fails, instead of bubbling to the
 * root app/error.tsx and taking the whole shell down.
 */
export default function Error({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  return <RouteErrorFallback error={error} reset={reset} />;
}
