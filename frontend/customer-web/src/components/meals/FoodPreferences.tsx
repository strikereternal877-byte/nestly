"use client";

import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { ChoiceChips, chipClass } from "@/components/meals/MealParts";
import { Card, cx } from "@/components/ui";
import { API_V1, apiFetch } from "@/lib/api";
import {
  CUISINE_LABELS,
  Cuisine,
  DIET_LABELS,
  DietType,
  SPICE_LABELS,
  SpiceLevel,
} from "@/lib/meals";
import type { MealPreferences } from "@/lib/meals";

/** What a customer with nothing saved starts from (product owner, 2026-10-11: veg by default). */
const DEFAULT_PREFERENCES: MealPreferences = {
  diet: DietType.Veg,
  spice: SpiceLevel.Medium,
  cuisines: [],
};

const DIETS = Object.values(DietType).filter(
  (v): v is DietType => typeof v === "number",
);
const SPICES = Object.values(SpiceLevel).filter(
  (v): v is SpiceLevel => typeof v === "number",
);
const CUISINES = Object.values(Cuisine).filter(
  (v): v is Cuisine => typeof v === "number",
);

/**
 * The food preferences for a cook booking (docs/MEAL-SERVICE.md FOOD
 * PREFERENCES), starting from what the customer saved with their last cook
 * booking - or veg, medium spice, any cuisine - until they change it.
 */
export function useFoodPreferences(enabled = true) {
  const saved = useQuery({
    queryKey: ["meal-preferences"],
    // 204 (nothing saved yet) arrives as undefined, which a query can't hold.
    queryFn: async () =>
      (await apiFetch<MealPreferences>(`${API_V1}/profile/meal-preferences`, {
        authenticated: true,
      })) ?? null,
    staleTime: 5 * 60_000,
    enabled,
  });
  const [draft, setDraft] = useState<MealPreferences | null>(null);
  const value = draft ?? saved.data ?? DEFAULT_PREFERENCES;
  return { value, onChange: setDraft };
}

/** The same card on checkout and the monthly review page. */
export function FoodPreferencesCard({
  value,
  onChange,
}: {
  value: MealPreferences;
  onChange: (next: MealPreferences) => void;
}) {
  const toggleCuisine = (cuisine: Cuisine) =>
    onChange({
      ...value,
      cuisines: value.cuisines.includes(cuisine)
        ? value.cuisines.filter((c) => c !== cuisine)
        : [...value.cuisines, cuisine].sort((a, b) => a - b),
    });

  return (
    <Card
      title="Food preferences"
      description="Your cook cooks this way. We keep it for your next booking."
    >
      <div className="flex flex-col gap-5">
        <ChoiceChips
          label="Food"
          options={DIETS.map((d) => ({
            value: String(d),
            label: DIET_LABELS[d],
          }))}
          value={String(value.diet)}
          onChange={(next) =>
            onChange({ ...value, diet: Number(next) as DietType })
          }
        />
        <ChoiceChips
          label="Spice"
          options={SPICES.map((s) => ({
            value: String(s),
            label: SPICE_LABELS[s],
          }))}
          value={String(value.spice)}
          onChange={(next) =>
            onChange({ ...value, spice: Number(next) as SpiceLevel })
          }
        />
        <div className="flex flex-col gap-2">
          <span className="text-sm text-fg-muted">Cuisine</span>
          <div
            role="group"
            aria-label="Cuisine"
            className="flex flex-wrap gap-2"
          >
            {CUISINES.map((cuisine) => {
              const on = value.cuisines.includes(cuisine);
              return (
                <button
                  key={cuisine}
                  type="button"
                  aria-pressed={on}
                  onClick={() => toggleCuisine(cuisine)}
                  className={cx(
                    "rounded-full px-3.5 py-1.5 text-sm",
                    chipClass(on),
                  )}
                >
                  {CUISINE_LABELS[cuisine]}
                </button>
              );
            })}
          </div>
          <p className="text-xs text-fg-muted">
            {value.cuisines.length
              ? "Pick as many as you like."
              : "None picked: your cook makes any."}
          </p>
        </div>
      </div>
    </Card>
  );
}
