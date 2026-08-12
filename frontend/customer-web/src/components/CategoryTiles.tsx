"use client";

import { useQuery } from "@tanstack/react-query";
import { motion } from "motion/react";
import { useState } from "react";
import { CategoryGridSkeleton } from "@/components/CategoryGridSkeleton";
import { CategoryTile } from "@/components/CategoryTile";
import { CitySelector } from "@/components/CitySelector";
import { Reveal, revealItem } from "@/components/motion";
import { Alert, Button, EmptyState } from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import type { CategorySummary } from "@/lib/types";

/**
 * Category tile grid, filtered to the customer's selected city (SRS 11.1.3 -
 * "homepage shall display service categories filtered by selected
 * city/serviceability where applicable").
 */
export function CategoryTiles() {
  const { city } = useSelectedCity();

  // `undefined` means the persisted city is still being read; showing the
  // "pick a city" prompt here would flash it at every customer who already has one.
  if (city === undefined) {
    return <CategoryGridSkeleton />;
  }

  if (city === null) {
    return <NoCitySelectedPrompt />;
  }

  return <CityCategoryGrid cityId={city.id} />;
}

function NoCitySelectedPrompt() {
  return (
    <EmptyState
      icon={
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="1.75"
          strokeLinecap="round"
          strokeLinejoin="round"
          className="h-5 w-5"
          aria-hidden
        >
          <path d="M12 21s7-6.4 7-11a7 7 0 1 0-14 0c0 4.6 7 11 7 11Z" />
          <circle cx="12" cy="10" r="2.5" />
        </svg>
      }
      title="Choose your city"
      description="Services and pricing vary by city — tell us where you are and we'll show what's available near you."
      action={<CitySelector />}
    />
  );
}

function CityCategoryGrid({ cityId }: { cityId: string }) {
  const [showAll, setShowAll] = useState(false);
  const query = useQuery({
    queryKey: ["categories", cityId],
    queryFn: () => apiFetch<CategorySummary[]>(`${API_V1}/categories?cityId=${cityId}`),
  });

  if (query.isPending) {
    return <CategoryGridSkeleton />;
  }

  if (query.isError) {
    return (
      <Alert
        tone="error"
        title="Couldn't load categories"
        action={
          <Button size="sm" variant="secondary" onClick={() => query.refetch()}>
            Retry
          </Button>
        }
      >
        {describeError(query.error)}
      </Alert>
    );
  }

  if (query.data.length === 0) {
    return (
      <EmptyState
        title="No services here yet"
        description="We're not live in your city yet — try another city, or check back soon."
        action={<CitySelector />}
      />
    );
  }

  const visible = showAll ? query.data : query.data.slice(0, 8);

  return (
    <div className="flex flex-col gap-6">
      <Reveal className="grid grid-cols-2 gap-4 sm:grid-cols-3 sm:gap-6 lg:grid-cols-4 lg:gap-8">
        {visible.map((category) => (
          <motion.div key={category.id} variants={revealItem}>
            <CategoryTile category={category} />
          </motion.div>
        ))}
      </Reveal>

      {!showAll && query.data.length > visible.length ? (
        <Button variant="secondary" className="mx-auto" onClick={() => setShowAll(true)}>
          Show all {query.data.length} categories
        </Button>
      ) : null}
    </div>
  );
}
