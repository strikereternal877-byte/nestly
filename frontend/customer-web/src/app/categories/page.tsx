"use client";

import { useQuery } from "@tanstack/react-query";
import { motion } from "motion/react";
import { CategoryGridSkeleton } from "@/components/CategoryGridSkeleton";
import { CategoryTile } from "@/components/CategoryTile";
import { CitySelector } from "@/components/CitySelector";
import { Reveal, revealItem } from "@/components/motion";
import { Alert, Button, EmptyState, PageHeading } from "@/components/ui";
import { useSelectedCity } from "@/hooks/useSelectedCity";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import type { CategorySummary } from "@/lib/types";

/** Category listing page (SRS 11.5.1), filtered to the customer's selected city. */
export default function CategoriesPage() {
  const { city } = useSelectedCity();

  return (
    <main className="mx-auto w-full max-w-7xl px-4 py-8 sm:px-6 sm:py-12">
      <PageHeading
        title="All categories"
        subtitle="Browse every service we offer in your city."
        actions={city ? <CitySelector /> : undefined}
      />

      {/* `undefined` is "still reading the persisted city", `null` is "read,
          none chosen" — collapsing them would flash the picker at customers
          who already have a city saved. */}
      {city === undefined ? (
        <CategoryGridSkeleton />
      ) : city === null ? (
        <EmptyState
          title="Choose your city"
          description="Services and pricing vary by city — tell us where you are and we'll show what's available near you."
          action={<CitySelector />}
        />
      ) : (
        <CategoryGrid cityId={city.id} />
      )}
    </main>
  );
}

function CategoryGrid({ cityId }: { cityId: string }) {
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

  return (
    <Reveal className="grid grid-cols-2 gap-4 sm:grid-cols-3 sm:gap-6 lg:grid-cols-4 lg:gap-8">
      {query.data.map((category) => (
        <motion.div key={category.id} variants={revealItem}>
          <CategoryTile category={category} />
        </motion.div>
      ))}
    </Reveal>
  );
}
