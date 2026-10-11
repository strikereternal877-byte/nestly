import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { MealType, mealsFromParam, mealsToParam, quoteMeals, sessionContains } from "./meals";
import type { MealOption } from "./meals";

/**
 * The storefront's copy of the server's meal pricing (MealPricing.Quote): the
 * same numbers MealPricingTests.cs pins, so the live price a customer sees and
 * the price the server charges stay one rule.
 */

const COOK: MealOption[] = [
  { meal: MealType.Breakfast, baseRate: 100, includedPersons: 2, extraPersonRate: 30, baseMinutes: 45, minutesPerExtraPerson: 10, defaultStartTime: "07:30" },
  { meal: MealType.Lunch, baseRate: 150, includedPersons: 2, extraPersonRate: 40, baseMinutes: 60, minutesPerExtraPerson: 10, defaultStartTime: "12:00" },
  { meal: MealType.Dinner, baseRate: 150, includedPersons: 2, extraPersonRate: 40, baseMinutes: 60, minutesPerExtraPerson: 10, defaultStartTime: "19:00" },
];

describe("quoteMeals", () => {
  it("charges extra only for people beyond those included", () => {
    assert.equal(quoteMeals(COOK, [MealType.Lunch], 2)[0].rate, 150);
    const five = quoteMeals(COOK, [MealType.Lunch], 5)[0];
    assert.equal(five.rate, 150 + 3 * 40);
    assert.equal(five.durationMinutes, 60 + 3 * 10);
  });

  it("splits morning and evening meals into two visits, morning first", () => {
    const quotes = quoteMeals(COOK, [MealType.Dinner, MealType.Lunch, MealType.Breakfast], 3);
    assert.deepEqual(
      quotes.map((q) => [q.session, q.meals, q.rate, q.durationMinutes, q.defaultStartTime]),
      [
        ["morning", [MealType.Breakfast, MealType.Lunch], 130 + 190, 55 + 70, "07:30"],
        ["evening", [MealType.Dinner], 190, 70, "19:00"],
      ],
    );
  });

  it("ignores meals the service does not offer", () => {
    assert.deepEqual(quoteMeals(COOK, [MealType.Snacks], 2), []);
  });
});

describe("sessions and URL form", () => {
  it("puts 3 PM and later in the evening", () => {
    assert.equal(sessionContains("morning", "14:59"), true);
    assert.equal(sessionContains("evening", "15:00"), true);
    assert.equal(sessionContains("morning", "15:00"), false);
  });

  it("round-trips a selection through the URL and drops junk", () => {
    assert.equal(mealsToParam([MealType.Dinner, MealType.Breakfast]), "0.3");
    assert.deepEqual(mealsFromParam("0.3"), [MealType.Breakfast, MealType.Dinner]);
    assert.deepEqual(mealsFromParam("1.x.9"), [MealType.Lunch]);
  });
});
