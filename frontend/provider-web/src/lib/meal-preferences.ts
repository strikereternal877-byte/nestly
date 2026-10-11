/**
 * How a household likes its food, as booked with a cook (docs/MEAL-SERVICE.md
 * FOOD PREFERENCES). Ordinals match the backend's DietType / SpiceLevel /
 * Cuisine - they cross the wire as numbers.
 */
export interface MealPreferences {
  diet: number;
  spice: number;
  cuisines: number[];
}

const DIETS = ["Veg", "Non-veg", "Egg", "Jain", "No onion, garlic"];
const SPICES = ["Mild", "Medium", "Spicy"];
const CUISINES = ["North Indian", "Rajasthani", "South Indian", "Gujarati", "Punjabi", "Bengali"];

/** "Jain · mild · Rajasthani, Gujarati" */
export function describePreferences(preferences: MealPreferences): string {
  const cuisines = preferences.cuisines.length
    ? preferences.cuisines.map((c) => CUISINES[c] ?? "Other").join(", ")
    : "any cuisine";
  return `${DIETS[preferences.diet] ?? "Any"} · ${(SPICES[preferences.spice] ?? "Medium").toLowerCase()} · ${cuisines}`;
}
