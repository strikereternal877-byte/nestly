"use client";

import { useMutation } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { Alert, Card, Field, Skeleton } from "@/components/ui";
import { DescriptionList, FormGrid } from "@/components/data-table";
import { describeError } from "@/lib/api";
import type { NotificationTemplatePreviewResponse } from "@/lib/notification-template-types";

const PLACEHOLDER_PATTERN = /\{\{(\w+)\}\}/g;

/** Extracts the distinct `{{Variable}}` names referenced across the given text(s), in first-seen order. */
function extractVariableNames(...texts: Array<string | null | undefined>): string[] {
  const seen = new Set<string>();
  for (const text of texts) {
    if (!text) continue;
    for (const match of Array.from(text.matchAll(PLACEHOLDER_PATTERN))) {
      seen.add(match[1]);
    }
  }
  return Array.from(seen);
}

/** True when both maps hold the same keys bound to the same values. */
function sameValues(a: Record<string, string>, b: Record<string, string>): boolean {
  const aKeys = Object.keys(a);
  if (aKeys.length !== Object.keys(b).length) return false;
  return aKeys.every((key) => a[key] === b[key]);
}

interface PreviewProps {
  /** Included in the effect's dependency list so switching channel (which flips subject-required-ness) re-triggers a render. */
  channel: number;
  subject: string | null;
  body: string;
  render: (sampleVariables: Record<string, string>) => Promise<NotificationTemplatePreviewResponse>;
}

/**
 * Live "preview/test" rendering shared by the template editor's draft preview
 * and the templates table's saved-template preview (SRS 12.17.2, task 126b).
 * The two callers differ only in which endpoint renders the text - ad-hoc for
 * an unsaved draft, saved-id for a persisted row - so that's the one thing left
 * as a prop; everything else (placeholder discovery, sample-value inputs,
 * debounced re-render) is identical and lives here once.
 *
 * Exported twice: `…Panel` carries its own `Card` for the side-by-side editor
 * layout, `…Body` is the same content bare, for the row-level preview `Modal`
 * that supplies its own heading and chrome.
 */
export function NotificationTemplatePreviewBody({ channel, subject, body, render }: PreviewProps) {
  const variableNames = useMemo(() => extractVariableNames(subject, body), [subject, body]);
  const [sampleValues, setSampleValues] = useState<Record<string, string>>({});

  // Seed a friendly default for every placeholder currently referenced, and
  // drop any that no longer appear - without clobbering an admin's own edits
  // to the ones that remain.
  //
  // `variableNames` is a fresh array on every subject/body change, so leaving
  // `sampleValues` untouched when nothing actually moved (via `sameValues`
  // below) matters here specifically to avoid re-triggering the debounced
  // render effect a second time per character. Adjusting state when a prop
  // changes (react.dev/learn/you-might-not-need-an-effect), not an effect.
  const [seededVariableNames, setSeededVariableNames] = useState(variableNames);
  if (variableNames !== seededVariableNames) {
    setSeededVariableNames(variableNames);
    const next: Record<string, string> = {};
    for (const name of variableNames) {
      next[name] = sampleValues[name] ?? `Sample ${name}`;
    }
    if (!sameValues(sampleValues, next)) setSampleValues(next);
  }

  const previewMutation = useMutation({
    mutationFn: (sampleVariables: Record<string, string>) => render(sampleVariables),
  });

  // Debounced (300ms) so a fast typist doesn't fire a request per keystroke;
  // an empty body has nothing worth rendering (and often fails Update's
  // required-body validation before the admin has finished typing).
  useEffect(() => {
    if (body.trim() === "") return;
    const timeout = setTimeout(() => previewMutation.mutate(sampleValues), 300);
    return () => clearTimeout(timeout);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [channel, subject, body, sampleValues]);

  return (
    <div className="flex flex-col gap-4">
      {variableNames.length > 0 ? (
        <FormGrid>
          {variableNames.map((name) => (
            <Field
              key={name}
              label={`{{${name}}}`}
              value={sampleValues[name] ?? ""}
              onChange={(event) =>
                setSampleValues((previous) => ({ ...previous, [name]: event.target.value }))
              }
            />
          ))}
        </FormGrid>
      ) : (
        <p className="text-sm text-fg-muted">
          No <code className="rounded bg-surface-3 px-1 py-0.5 text-xs">{"{{Variable}}"}</code>{" "}
          placeholders in the current text.
        </p>
      )}

      {previewMutation.isError ? <Alert>{describeError(previewMutation.error)}</Alert> : null}

      {previewMutation.data ? (
        <div className="rounded-xl border border-line bg-surface-2 p-4">
          <DescriptionList
            columns={1}
            items={[
              ...(previewMutation.data.subject
                ? [{ label: "Subject", value: <span className="font-medium">{previewMutation.data.subject}</span> }]
                : []),
              {
                label: "Body",
                value: (
                  <span className="block whitespace-pre-wrap leading-relaxed">{previewMutation.data.body}</span>
                ),
              },
            ]}
          />
        </div>
      ) : body.trim() === "" ? (
        <p className="text-sm text-fg-muted">Nothing to preview yet — start typing a body.</p>
      ) : (
        // The first render is in flight (or still debouncing). A placeholder
        // the shape of the rendered box keeps the panel from collapsing and
        // jumping back open.
        <div className="flex flex-col gap-3 rounded-xl border border-line bg-surface-2 p-4">
          <Skeleton className="h-3 w-16" />
          <Skeleton className="h-4 w-full" />
          <Skeleton className="h-4 w-4/5" />
        </div>
      )}
    </div>
  );
}

export function NotificationTemplatePreviewPanel(props: PreviewProps) {
  return (
    <Card
      title="Preview"
      description="Rendered against sample values — nothing is sent or persisted (SRS 12.17.2)."
    >
      <NotificationTemplatePreviewBody {...props} />
    </Card>
  );
}
