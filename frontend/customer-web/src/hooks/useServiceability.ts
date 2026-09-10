"use client";

import { useQuery } from "@tanstack/react-query";

import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch } from "@/lib/api";
import type { ServiceabilityResult } from "@/lib/types";

/**
 * Whether this service is offered at the customer's selected locality.
 *
 * Shared by the availability panel and the page's booking CTA so the two
 * cannot disagree - previously only the panel knew the answer, which left
 * "Book now" fully enabled directly beneath a "Not available here" error.
 * The query key matches the panel's, so TanStack Query serves both readers
 * from one request rather than issuing a second.
 */
export function useServiceability(serviceId: string) {
  const { locality } = useSelectedCity();
  const localityId = locality ? locality.id : null;

  const query = useQuery({
    queryKey: ["serviceability", "service", serviceId, localityId],
    queryFn: () =>
      apiFetch<ServiceabilityResult>(
        `${API_V1}/serviceability/services/${serviceId}?localityId=${localityId}`,
      ),
    enabled: localityId !== null,
  });

  return {
    /** True only once the API has positively said no - never while loading or on error. */
    isUnserviceable: query.data ? !query.data.isServiceable : false,
    isServiceable: query.data ? query.data.isServiceable : false,
    /** No locality picked yet, so the question has not been asked. */
    isUnknown: localityId === null || query.isPending,
    /**
     * Surfaced so the availability panel can render a distinct error state
     * with retry, rather than only ever showing loading or the (functionally
     * identical) "not serviceable" copy for a request that actually failed.
     */
    isError: query.isError,
    error: query.error,
    refetch: query.refetch,
  };
}
