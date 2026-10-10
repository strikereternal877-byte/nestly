"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorState } from "@/components/states";
import { Alert, Button, Card, IconButton, Skeleton, cx, useToast } from "@/components/ui";
import { getAvailability, updateAvailabilityWindows } from "@/lib/availability-api";
import { DAY_OF_WEEK_LABELS, DayOfWeek } from "@/lib/availability-types";
import type { AvailabilityWindowInput } from "@/lib/availability-types";

/** "09:00:00" (TimeSpan over the wire) <-> "09:00" (<input type="time"> value). */
const toTimeInputValue = (timeSpan: string) => timeSpan.slice(0, 5);
const toApiTimeValue = (inputValue: string) => (inputValue.length === 5 ? `${inputValue}:00` : inputValue);

/**
 * Work-week order rather than the enum's Sunday-first declaration order. The
 * wire values are unchanged - this only decides what a provider reads first.
 */
const DAY_ORDER: readonly DayOfWeek[] = [
  DayOfWeek.Monday,
  DayOfWeek.Tuesday,
  DayOfWeek.Wednesday,
  DayOfWeek.Thursday,
  DayOfWeek.Friday,
  DayOfWeek.Saturday,
  DayOfWeek.Sunday,
];

const DEFAULT_START = "09:00";
const DEFAULT_END = "17:00";

interface DraftWindow {
  dayOfWeek: DayOfWeek;
  startTime: string;
  endTime: string;
}

/**
 * Weekly availability windows editor (docs/PROVIDER.md's `provider_availability`
 * table). The API is a full-replace PUT, so this edits a local draft list and
 * only sends it on "Save changes".
 *
 * Laid out as seven fixed day rows rather than a list of add-a-row forms with
 * a day dropdown each. A provider setting their week thinks in days, not in
 * rows, and the previous shape made "am I working Thursday?" a question you
 * had to answer by reading every row. Each day is a single large toggle and
 * its hours are two full-height time fields, all sized for a thumb.
 */
export function WindowsSection() {
  const queryClient = useQueryClient();
  const toast = useToast();
  const [rows, setRows] = useState<DraftWindow[]>([]);
  const [isDirty, setIsDirty] = useState(false);

  const query = useQuery({ queryKey: ["provider-availability"], queryFn: getAvailability });

  // Adjusting state when a prop changes (react.dev/learn/you-might-not-need-an-effect).
  const [seededFor, setSeededFor] = useState({ data: query.data, isDirty });
  if (seededFor.data !== query.data || seededFor.isDirty !== isDirty) {
    setSeededFor({ data: query.data, isDirty });
    if (query.data && !isDirty) {
      setRows(
        query.data.windows.map((w) => ({
          dayOfWeek: w.dayOfWeek,
          startTime: toTimeInputValue(w.startTime),
          endTime: toTimeInputValue(w.endTime),
        })),
      );
    }
  }

  const mutation = useMutation({
    mutationFn: (windows: AvailabilityWindowInput[]) => updateAvailabilityWindows({ windows }),
    onSuccess: (windows) => {
      queryClient.setQueryData(["provider-availability"], (current: typeof query.data) =>
        current ? { ...current, windows } : current,
      );
      setIsDirty(false);
      toast("success", "Availability saved.");
    },
  });

  function mutateRows(next: (current: DraftWindow[]) => DraftWindow[]) {
    setIsDirty(true);
    setRows(next);
  }

  function updateWindow(target: DraftWindow, patch: Partial<DraftWindow>) {
    mutateRows((current) => current.map((row) => (row === target ? { ...row, ...patch } : row)));
  }

  function removeWindow(target: DraftWindow) {
    mutateRows((current) => current.filter((row) => row !== target));
  }

  function addWindow(day: DayOfWeek) {
    mutateRows((current) => [
      ...current,
      { dayOfWeek: day, startTime: DEFAULT_START, endTime: DEFAULT_END },
    ]);
  }

  function setDayEnabled(day: DayOfWeek, enabled: boolean) {
    mutateRows((current) => {
      const withoutDay = current.filter((row) => row.dayOfWeek !== day);
      return enabled
        ? [...withoutDay, { dayOfWeek: day, startTime: DEFAULT_START, endTime: DEFAULT_END }]
        : withoutDay;
    });
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

  // An end at or before the start is not a window the API will accept, and
  // silently posting it just trades an inline hint for a round-trip error.
  const invalidCount = rows.filter((row) => row.endTime <= row.startTime).length;

  if (query.isPending) {
    return (
      <Card title="Weekly availability">
        <div className="flex flex-col gap-2.5">
          {Array.from({ length: 7 }, (_, index) => (
            <Skeleton key={index} className="h-16 w-full rounded-xl" />
          ))}
        </div>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Weekly availability">
        <ErrorState
          title="Couldn't load your availability"
          error={query.error}
          onRetry={() => query.refetch()}
          isRetrying={query.isRefetching}
        />
      </Card>
    );
  }

  return (
    <Card
      title="Weekly availability"
      description="The recurring days and hours you're available for jobs."
    >
      <div className="flex flex-col gap-4">
        {mutation.isError ? (
          <ErrorState title="Couldn't save your availability" error={mutation.error} />
        ) : null}

        {rows.length === 0 ? (
          <Alert tone="warning" title="You have no availability set">
            Turn on the days you can work — without any hours you won&apos;t be matched to jobs.
          </Alert>
        ) : null}

        <div className="flex flex-col gap-2.5">
          {DAY_ORDER.map((day) => (
            <DayRow
              key={day}
              day={day}
              windows={rows.filter((row) => row.dayOfWeek === day)}
              onToggle={(enabled) => setDayEnabled(day, enabled)}
              onAdd={() => addWindow(day)}
              onUpdate={updateWindow}
              onRemove={removeWindow}
            />
          ))}
        </div>

        {invalidCount > 0 ? (
          <Alert tone="warning">
            {invalidCount === 1
              ? "One window ends before it starts. Fix it to save."
              : `${invalidCount} windows end before they start. Fix them to save.`}
          </Alert>
        ) : null}

        <div className="flex items-center justify-between gap-3 border-t border-line pt-4">
          <p className="text-sm text-fg-muted" aria-live="polite">
            {isDirty ? "You have unsaved changes." : "All changes saved."}
          </p>
          <Button
            type="button"
            loading={mutation.isPending}
            disabled={!isDirty || invalidCount > 0}
            onClick={save}
          >
            Save changes
          </Button>
        </div>
      </div>
    </Card>
  );
}

/** One day of the week, with however many windows it carries. */
function DayRow({
  day,
  windows,
  onToggle,
  onAdd,
  onUpdate,
  onRemove,
}: {
  day: DayOfWeek;
  windows: DraftWindow[];
  onToggle: (enabled: boolean) => void;
  onAdd: () => void;
  onUpdate: (target: DraftWindow, patch: Partial<DraftWindow>) => void;
  onRemove: (target: DraftWindow) => void;
}) {
  const enabled = windows.length > 0;
  const label = DAY_OF_WEEK_LABELS[day];

  return (
    <div
      className={cx(
        "rounded-xl border p-3 transition-colors duration-fast ease-out",
        enabled ? "border-line bg-surface-2" : "border-line bg-surface",
      )}
    >
      <div className="flex items-center justify-between gap-3">
        {/* The whole label is the hit area: the switch alone is a small target
            on a phone held in one hand. */}
        <label className="flex flex-1 cursor-pointer items-center gap-3">
          <input
            type="checkbox"
            role="switch"
            checked={enabled}
            onChange={(e) => onToggle(e.target.checked)}
            className="peer sr-only"
          />
          <span
            aria-hidden
            className={cx(
              "relative h-6 w-11 shrink-0 rounded-full transition-colors duration-fast ease-out",
              "peer-focus-visible:ring-2 peer-focus-visible:ring-brand-600/40 peer-focus-visible:ring-offset-2 peer-focus-visible:ring-offset-bg",
              enabled ? "bg-brand-600" : "bg-surface-3",
            )}
          >
            <span
              className={cx(
                "absolute top-0.5 h-5 w-5 rounded-full bg-surface shadow-sm transition-all duration-fast ease-out",
                enabled ? "left-[1.375rem]" : "left-0.5",
              )}
            />
          </span>
          <span
            className={cx(
              "text-sm font-medium",
              enabled ? "text-fg" : "text-fg-subtle",
            )}
          >
            {label}
          </span>
        </label>

        {!enabled ? <span className="text-xs text-fg-subtle">Not working</span> : null}
      </div>

      {enabled ? (
        <div className="mt-3 flex flex-col gap-2 border-t border-line pt-3">
          {windows.map((window, index) => {
            const invalid = window.endTime <= window.startTime;
            return (
              <div key={index} className="flex items-center gap-2">
                <TimeInput
                  label={`${label} window ${index + 1} start time`}
                  value={window.startTime}
                  invalid={invalid}
                  onChange={(value) => onUpdate(window, { startTime: value })}
                />
                <span aria-hidden className="text-sm text-fg-subtle">
                  –
                </span>
                <TimeInput
                  label={`${label} window ${index + 1} end time`}
                  value={window.endTime}
                  invalid={invalid}
                  onChange={(value) => onUpdate(window, { endTime: value })}
                />
                <IconButton
                  label={`Remove ${label} window ${index + 1}`}
                  onClick={() => onRemove(window)}
                  className="ml-auto shrink-0"
                >
                  <svg
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    className="h-4 w-4"
                    aria-hidden
                  >
                    <path d="M18 6 6 18M6 6l12 12" />
                  </svg>
                </IconButton>
              </div>
            );
          })}

          <Button type="button" variant="ghost" size="sm" className="w-fit" onClick={onAdd}>
            + Add hours
          </Button>
        </div>
      ) : null}
    </div>
  );
}

/**
 * Time field sized for a thumb (44px tall, comfortably over the minimum touch
 * target) and styled from the tokens, since the kit has no time control.
 */
function TimeInput({
  label,
  value,
  invalid,
  onChange,
}: {
  label: string;
  value: string;
  invalid: boolean;
  onChange: (value: string) => void;
}) {
  return (
    <input
      type="time"
      aria-label={label}
      aria-invalid={invalid || undefined}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className={cx(
        "nums h-11 min-w-0 flex-1 rounded-lg border bg-surface px-3 text-sm text-fg shadow-xs outline-none transition duration-fast ease-out",
        invalid
          ? "border-danger focus:border-danger focus:ring-2 focus:ring-danger/25"
          : "border-line hover:border-line-strong focus:border-brand-600 focus:ring-2 focus:ring-brand-600/25",
      )}
    />
  );
}
