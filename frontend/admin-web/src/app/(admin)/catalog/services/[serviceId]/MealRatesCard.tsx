"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { FormActions, FormGrid } from "@/components/data-table";
import { Alert, Button, Card, CheckboxField, Field } from "@/components/ui";
import { describeError } from "@/lib/api";
import { setServiceMealOptions } from "@/lib/catalog-api";
import { MealType } from "@/lib/catalog-types";
import type { MealOptionDto, ServiceAdminResponse } from "@/lib/catalog-types";

const MEALS: { meal: MealType; label: string; usualStart: string }[] = [
  { meal: MealType.Breakfast, label: "Breakfast", usualStart: "07:30" },
  { meal: MealType.Lunch, label: "Lunch", usualStart: "12:00" },
  { meal: MealType.Snacks, label: "Evening snacks", usualStart: "16:30" },
  { meal: MealType.Dinner, label: "Dinner", usualStart: "19:00" },
];

interface Row {
  offered: boolean;
  baseRate: string;
  includedPersons: string;
  extraPersonRate: string;
  baseMinutes: string;
  minutesPerExtraPerson: string;
  defaultStartTime: string;
}

function toRows(options: MealOptionDto[] | null): Row[] {
  return MEALS.map(({ meal, usualStart }) => {
    const option = options?.find((o) => o.meal === meal);
    return option
      ? {
          offered: true,
          baseRate: String(option.baseRate),
          includedPersons: String(option.includedPersons),
          extraPersonRate: String(option.extraPersonRate),
          baseMinutes: String(option.baseMinutes),
          minutesPerExtraPerson: String(option.minutesPerExtraPerson),
          defaultStartTime: option.defaultStartTime,
        }
      : {
          offered: false,
          baseRate: "",
          includedPersons: "2",
          extraPersonRate: "0",
          baseMinutes: "60",
          minutesPerExtraPerson: "10",
          defaultStartTime: usualStart,
        };
  });
}

/**
 * Meal rates of a MealBased service (docs/MEAL-SERVICE.md): per meal, a rate
 * covering some people, a charge per extra person, and how long cooking
 * takes. Saved as a whole; the server validates every rule (session times,
 * ranges) and sets the listing price to the cheapest meal.
 */
export function MealRatesCard({ service, canWrite }: { service: ServiceAdminResponse; canWrite: boolean }) {
  const queryClient = useQueryClient();
  const [rows, setRows] = useState<Row[]>(() => toRows(service.mealOptions));

  const mutation = useMutation({
    mutationFn: (options: MealOptionDto[]) => setServiceMealOptions(service.id, options),
    onSuccess: (updated) => queryClient.setQueryData(["services", service.id], updated),
  });

  const update = (index: number, patch: Partial<Row>) =>
    setRows((current) => current.map((row, i) => (i === index ? { ...row, ...patch } : row)));

  const save = () =>
    mutation.mutate(
      rows.flatMap((row, i) =>
        row.offered
          ? [
              {
                meal: MEALS[i].meal,
                baseRate: Number(row.baseRate),
                includedPersons: Number(row.includedPersons),
                extraPersonRate: Number(row.extraPersonRate),
                baseMinutes: Number(row.baseMinutes),
                minutesPerExtraPerson: Number(row.minutesPerExtraPerson),
                defaultStartTime: row.defaultStartTime,
              },
            ]
          : [],
      ),
    );

  return (
    <Card
      title="Meal rates"
      description="Customers pick meals and how many people; the price and visit length come from here. Breakfast and lunch are morning visits (start before 3 PM), snacks and dinner evening visits."
    >
      <div className="flex flex-col gap-5">
        {MEALS.map(({ label }, i) => {
          const row = rows[i];
          return (
            <div key={label} className="flex flex-col gap-3 border-b border-line pb-5 last:border-b-0 last:pb-0">
              <CheckboxField
                label={`Offer ${label.toLowerCase()}`}
                checked={row.offered}
                onChange={(offered) => update(i, { offered })}
                disabled={!canWrite}
              />
              {row.offered ? (
                <FormGrid>
                  <Field
                    label="Rate (₹)"
                    type="number"
                    min={1}
                    value={row.baseRate}
                    onChange={(e) => update(i, { baseRate: e.target.value })}
                    disabled={!canWrite}
                  />
                  <Field
                    label="Rate covers people"
                    type="number"
                    min={1}
                    max={12}
                    value={row.includedPersons}
                    onChange={(e) => update(i, { includedPersons: e.target.value })}
                    disabled={!canWrite}
                  />
                  <Field
                    label="Each extra person (₹)"
                    type="number"
                    min={0}
                    value={row.extraPersonRate}
                    onChange={(e) => update(i, { extraPersonRate: e.target.value })}
                    disabled={!canWrite}
                  />
                  <Field
                    label="Cooking time (min)"
                    type="number"
                    min={15}
                    max={480}
                    value={row.baseMinutes}
                    onChange={(e) => update(i, { baseMinutes: e.target.value })}
                    disabled={!canWrite}
                  />
                  <Field
                    label="Extra minutes per person"
                    type="number"
                    min={0}
                    max={60}
                    value={row.minutesPerExtraPerson}
                    onChange={(e) => update(i, { minutesPerExtraPerson: e.target.value })}
                    disabled={!canWrite}
                  />
                  <Field
                    label="Usual start time"
                    type="time"
                    value={row.defaultStartTime}
                    onChange={(e) => update(i, { defaultStartTime: e.target.value })}
                    disabled={!canWrite}
                  />
                </FormGrid>
              ) : null}
            </div>
          );
        })}

        {mutation.isError ? (
          <Alert tone="error" title="Couldn't save meal rates">
            {describeError(mutation.error)}
          </Alert>
        ) : null}
        {mutation.isSuccess ? (
          <Alert tone="success" title="Meal rates saved">
            The listing price is now the cheapest meal.
          </Alert>
        ) : null}

        {canWrite ? (
          <FormActions>
            <Button onClick={save} loading={mutation.isPending} disabled={!rows.some((r) => r.offered)}>
              Save meal rates
            </Button>
          </FormActions>
        ) : null}
      </div>
    </Card>
  );
}
