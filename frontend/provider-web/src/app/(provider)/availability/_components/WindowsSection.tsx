"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Alert, Button, Card, Select } from "@/components/ui";
import { describeError } from "@/lib/api";
import { getAvailability, updateAvailabilityWindows } from "@/lib/availability-api";
import { ALL_DAYS_OF_WEEK, DAY_OF_WEEK_LABELS, DayOfWeek } from "@/lib/availability-types";
import type { AvailabilityWindowInput } from "@/lib/availability-types";

/** "09:00:00" (TimeSpan over the wire) <-> "09:00" (<input type="time"> value). */
const toTimeInputValue = (timeSpan: string) => timeSpan.slice(0, 5);
const toApiTimeValue = (inputValue: string) => (inputValue.length === 5 ? `${inputValue}:00` : inputValue);

const DAY_OPTIONS = ALL_DAYS_OF_WEEK.map((day) => ({ value: String(day), label: DAY_OF_WEEK_LABELS[day] }));

interface DraftWindow {
  dayOfWeek: DayOfWeek;
  startTime: string;
  endTime: string;
}

const EMPTY_ROW: DraftWindow = { dayOfWeek: DayOfWeek.Monday, startTime: "09:00", endTime: "17:00" };

/**
 * Weekly availability windows editor (docs/PROVIDER.md's `provider_availability`
 * table). The API is a full-replace PUT, so this edits a local draft list
 * and only sends it on "Save changes".
 */
export function WindowsSection() {
  const queryClient = useQueryClient();
  const [rows, setRows] = useState<DraftWindow[]>([]);
  const [isDirty, setIsDirty] = useState(false);

  const query = useQuery({ queryKey: ["provider-availability"], queryFn: getAvailability });

  useEffect(() => {
    if (query.data && !isDirty) {
      setRows(
        query.data.windows.map((w) => ({
          dayOfWeek: w.dayOfWeek,
          startTime: toTimeInputValue(w.startTime),
          endTime: toTimeInputValue(w.endTime),
        })),
      );
    }
  }, [query.data, isDirty]);

  const mutation = useMutation({
    mutationFn: (windows: AvailabilityWindowInput[]) => updateAvailabilityWindows({ windows }),
    onSuccess: (windows) => {
      queryClient.setQueryData(["provider-availability"], (current: typeof query.data) =>
        current ? { ...current, windows } : current,
      );
      setIsDirty(false);
    },
  });

  function updateRow(index: number, patch: Partial<DraftWindow>) {
    setIsDirty(true);
    setRows((current) => current.map((row, i) => (i === index ? { ...row, ...patch } : row)));
  }

  function removeRow(index: number) {
    setIsDirty(true);
    setRows((current) => current.filter((_, i) => i !== index));
  }

  function addRow() {
    setIsDirty(true);
    setRows((current) => [...current, { ...EMPTY_ROW }]);
  }

  function save() {
    mutation.mutate(
      rows.map((row) => ({
        dayOfWeek: row.dayOfWeek,
        startTime: toApiTimeValue(row.startTime),
        endTime: toApiTimeValue(row.endTime),
      })),
    );
  }

  if (query.isPending) {
    return (
      <Card title="Weekly availability">
        <p className="text-sm text-neutral-500">Loading availability…</p>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Weekly availability">
        <Alert>{describeError(query.error)}</Alert>
      </Card>
    );
  }

  return (
    <Card title="Weekly availability" description="The recurring days and hours you're available for jobs.">
      {mutation.isError ? (
        <div className="mb-3">
          <Alert>{describeError(mutation.error)}</Alert>
        </div>
      ) : null}

      {rows.length === 0 ? (
        <p className="mb-3 text-sm text-neutral-600 dark:text-neutral-400">No availability windows added yet.</p>
      ) : (
        <div className="flex flex-col gap-3">
          {rows.map((row, index) => (
            <div key={index} className="flex flex-wrap items-end gap-3">
              <div className="w-40">
                <Select
                  label="Day"
                  options={DAY_OPTIONS}
                  value={String(row.dayOfWeek)}
                  onChange={(e) => updateRow(index, { dayOfWeek: Number(e.target.value) as DayOfWeek })}
                />
              </div>
              <div className="w-32">
                <label className="mb-1.5 block text-sm font-medium">Start time</label>
                <input
                  type="time"
                  value={row.startTime}
                  onChange={(e) => updateRow(index, { startTime: e.target.value })}
                  className="rounded-lg border border-black/15 bg-transparent px-3 py-2 text-sm outline-none focus:border-black focus:ring-1 focus:ring-black dark:border-white/20 dark:focus:border-white dark:focus:ring-white"
                />
              </div>
              <div className="w-32">
                <label className="mb-1.5 block text-sm font-medium">End time</label>
                <input
                  type="time"
                  value={row.endTime}
                  onChange={(e) => updateRow(index, { endTime: e.target.value })}
                  className="rounded-lg border border-black/15 bg-transparent px-3 py-2 text-sm outline-none focus:border-black focus:ring-1 focus:ring-black dark:border-white/20 dark:focus:border-white dark:focus:ring-white"
                />
              </div>
              <Button type="button" variant="danger" onClick={() => removeRow(index)}>
                Remove
              </Button>
            </div>
          ))}
        </div>
      )}

      <div className="mt-4 flex gap-2">
        <Button type="button" variant="secondary" onClick={addRow}>
          Add window
        </Button>
        <Button type="button" disabled={mutation.isPending || !isDirty} onClick={save}>
          {mutation.isPending ? "Saving…" : "Save changes"}
        </Button>
      </div>
    </Card>
  );
}
