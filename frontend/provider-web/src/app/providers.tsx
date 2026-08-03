"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState } from "react";
import type { ReactNode } from "react";
import { ApiError } from "@/lib/api";

/** Mirrors admin-web/src/app/providers.tsx for consistency. */
export function Providers({ children }: { children: ReactNode }) {
  // Created in state, not at module scope: a module-level client would be
  // shared across all users of a single server process during SSR.
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            staleTime: 30_000,
            // 401/403/404/501 will not become successes on a second try;
            // only retry the transient failures.
            retry: (failureCount, error) => {
              if (error instanceof ApiError && error.status < 500) return false;
              return failureCount < 2;
            },
          },
        },
      }),
  );

  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}
