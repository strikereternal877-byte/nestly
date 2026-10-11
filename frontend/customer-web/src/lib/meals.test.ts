import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { MealType, closestWindow, discountedRate, startChoices, mealsFromParam, mealsToParam, quoteMeals, sessionContains } from "./meals";
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

describe("closestWindow", () => {
  const morning = { startTime: "09:00:00", endTime: "12:00:00" };
  const noon = { startTime: "12:00:00", endTime: "15:00:00" };
  const evening = { startTime: "15:00:00", endTime: "18:00:00" };

  it("picks the window the meal's usual time falls in", () => {
    assert.equal(closestWindow([morning, noon], "11:30"), morning);
    assert.equal(closestWindow([morning, noon], "12:00"), noon);
  });

  it("falls back to the window starting nearest the usual time", () => {
    assert.equal(closestWindow([evening], "19:00"), evening);
    assert.equal(closestWindow([morning, noon], "07:30"), morning);
  });

  it("is null with no windows", () => {
    assert.equal(closestWindow([], "12:00"), null);
  });
});

describe("discountedRate", () => {
  it("takes the plan's discount off and keeps paise", () => {
    assert.equal(discountedRate(150, 10), 135);
    assert.equal(discountedRate(155, 15), 131.75);
    assert.equal(discountedRate(150, 0), 150);
  });
});

describe("startChoices", () => {
  it("runs from an hour before the first meal to the last meal (MealPricingTests' rule)", () => {
    const morning = quoteMeals(COOK, [MealType.Breakfast, MealType.Lunch], 2)[0];
    const times = startChoices(morning);
    assert.equal(times[0], "06:30");
    assert.equal(times.at(-1), "12:00");
    assert.equal(times.length, 12);
  });

  it("gives one meal an hour either side", () => {
    const dinner = quoteMeals(COOK, [MealType.Dinner], 2)[0];
    assert.deepEqual(startChoices(dinner), ["18:00", "18:30", "19:00", "19:30", "20:00"]);
  });
});
