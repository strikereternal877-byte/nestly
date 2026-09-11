import { RouteLoadingSkeleton } from "@/components/screen-states";

/**
 * Route-segment loading UI (task: premium UX audit, "No per-route
 * loading.tsx anywhere in the app"). Next.js swaps this in automatically
 * while this segment's page is loading, replacing what used to be a
 * blank/frozen screen on a slow connection.
 */
export default function Loading() {
  return <RouteLoadingSkeleton />;
}
