"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Alert, Button, Card, Field } from "@/components/ui";
import { describeError } from "@/lib/api";
import { createBlackoutDate, deleteBlackoutDate, listBlackoutDates } from "@/lib/availability-api";

const blackoutSchema = z
  .object({
    startDate: z.string().min(1, "Start date is required"),
    endDate: z.string().min(1, "End date is required"),
    reason: z.string().max(500),
  })
  .refine((values) => values.endDate >= values.startDate, {
    message: "The start date must not be after the end date.",
    path: ["endDate"],
  });
type BlackoutFormValues = z.infer<typeof blackoutSchema>;

/** Ad hoc dates a provider is not available on (docs/PROVIDER.md's `provider_availability` blackout dates). */
export function BlackoutDatesSection() {
  const queryClient = useQueryClient();

  const query = useQuery({ queryKey: ["provider-blackout-dates"], queryFn: listBlackoutDates });

  const form = useForm<BlackoutFormValues>({
    resolver: zodResolver(blackoutSchema),
    defaultValues: { startDate: "", endDate: "", reason: "" },
  });

  const createMutation = useMutation({
    mutationFn: (values: BlackoutFormValues) =>
      createBlackoutDate({
        startDate: values.startDate,
        endDate: values.endDate,
        reason: values.reason.trim() === "" ? undefined : values.reason,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["provider-blackout-dates"] });
      form.reset({ startDate: "", endDate: "", reason: "" });
    },
  });

  const deleteMutation = useMutation({
    mutationFn: deleteBlackoutDate,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["provider-blackout-dates"] }),
  });

  const onSubmit = form.handleSubmit((values) => createMutation.mutate(values));

  return (
    <Card title="Blackout dates" description="Dates you're unavailable, even within your normal weekly windows.">
      {query.isPending ? (
        <p className="text-sm text-neutral-500">Loading blackout dates…</p>
      ) : query.isError ? (
        <Alert>{describeError(query.error)}</Alert>
      ) : query.data.length === 0 ? (
        <p className="text-sm text-neutral-600 dark:text-neutral-400">No blackout dates yet.</p>
      ) : (
        <div className="overflow-x-auto rounded-xl border border-black/10 dark:border-white/15">
          <table className="w-full min-w-[560px] text-left text-sm">
            <thead className="border-b border-black/10 bg-black/5 dark:border-white/15 dark:bg-white/5">
              <tr>
                <th className="px-3 py-2 font-medium">Dates</th>
                <th className="px-3 py-2 font-medium">Reason</th>
                <th className="px-3 py-2 font-medium"><span className="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              {query.data.map((blackout) => (
                <tr key={blackout.id} className="border-b border-black/5 last:border-0 dark:border-white/10">
                  <td className="px-3 py-2 tabular-nums">
                    {blackout.startDate === blackout.endDate
                      ? blackout.startDate
                      : `${blackout.startDate} – ${blackout.endDate}`}
                  </td>
                  <td className="px-3 py-2">{blackout.reason ?? "—"}</td>
                  <td className="px-3 py-2">
                    <Button
                      type="button"
                      variant="danger"
                      className="px-2 py-1 text-xs"
                      disabled={deleteMutation.isPending && deleteMutation.variables === blackout.id}
                      onClick={() => deleteMutation.mutate(blackout.id)}
                    >
                      {deleteMutation.isPending && deleteMutation.variables === blackout.id ? "Removing…" : "Remove"}
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <form onSubmit={onSubmit} className="mt-4 flex flex-wrap items-end gap-3" noValidate>
        {createMutation.isError ? (
          <div className="w-full">
            <Alert>{describeError(createMutation.error)}</Alert>
          </div>
        ) : null}
        {form.formState.errors.endDate ? (
          <div className="w-full">
            <Alert>{form.formState.errors.endDate.message}</Alert>
          </div>
        ) : null}
        <div className="w-40">
          <Field label="Start date" type="date" error={form.formState.errors.startDate?.message} {...form.register("startDate")} />
        </div>
        <div className="w-40">
          <Field label="End date" type="date" {...form.register("endDate")} />
        </div>
        <div className="w-64">
          <Field label="Reason (optional)" {...form.register("reason")} />
        </div>
        <Button type="submit" disabled={form.formState.isSubmitting || createMutation.isPending}>
          {createMutation.isPending ? "Adding…" : "Add blackout date"}
        </Button>
      </form>
    </Card>
  );
}
