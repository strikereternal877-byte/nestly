"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorState } from "@/components/states";
import {
  Alert,
  Button,
  Card,
  EmptyState,
  IconButton,
  Select,
  Skeleton,
  useToast,
} from "@/components/ui";
import { listCatalogCategories, listCatalogServices } from "@/lib/lookup-api";
import { getSkills, updateSkills } from "@/lib/profile-api";
import type { ProviderSkillInput } from "@/lib/profile-types";

const EMPTY_ROW: ProviderSkillInput = { categoryId: "", serviceId: "" };
const NO_SPECIFIC_SERVICE = "";

/**
 * Skills editor (docs/PROVIDER.md's Capability & Coverage domain,
 * `provider_skill_mapping`): the categories/services a provider is qualified to
 * fulfil. The API is a full-replace PUT, so this edits a local draft list and
 * only sends it on "Save changes" - the unsaved-changes line at the bottom is
 * what makes that model visible rather than surprising.
 *
 * Category and service are chosen from real name dropdowns (task 205, backed
 * by `/api/v1/catalog/{categories,services}`) rather than the hand-typed GUIDs
 * this screen originally shipped with. That behaviour is load-bearing: no
 * provider can type a catalog GUID from a phone in the field.
 */
export function SkillsSection() {
  const queryClient = useQueryClient();
  const toast = useToast();
  const [rows, setRows] = useState<ProviderSkillInput[]>([]);
  const [isDirty, setIsDirty] = useState(false);

  const query = useQuery({ queryKey: ["provider-skills"], queryFn: getSkills });
  const categoriesQuery = useQuery({
    queryKey: ["catalog-categories"],
    queryFn: listCatalogCategories,
  });
  // Fetched once, unfiltered, then filtered per-row client-side - simpler than
  // one query per row and the catalog is small enough that this is cheap.
  const servicesQuery = useQuery({
    queryKey: ["catalog-services"],
    queryFn: () => listCatalogServices(),
  });

  // Adjusting state when a prop changes (react.dev/learn/you-might-not-need-an-effect).
  const [seededFor, setSeededFor] = useState({ data: query.data, isDirty });
  if (seededFor.data !== query.data || seededFor.isDirty !== isDirty) {
    setSeededFor({ data: query.data, isDirty });
    if (query.data && !isDirty) {
      setRows(
        query.data.map((skill) => ({
          categoryId: skill.categoryId,
          serviceId: skill.serviceId ?? "",
        })),
      );
    }
  }

  const mutation = useMutation({
    mutationFn: (skills: ProviderSkillInput[]) => updateSkills({ skills }),
    onSuccess: (skills) => {
      queryClient.setQueryData(["provider-skills"], skills);
      setIsDirty(false);
      toast("success", "Skills saved.");
    },
  });

  function updateRow(index: number, patch: Partial<ProviderSkillInput>) {
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
    const skills = rows
      .filter((row) => row.categoryId.trim() !== "")
      .map((row) => ({
        categoryId: row.categoryId.trim(),
        serviceId: row.serviceId?.trim() || undefined,
      }));
    mutation.mutate(skills);
  }

  if (query.isPending || categoriesQuery.isPending || servicesQuery.isPending) {
    return (
      <Card title="Skills">
        <div className="flex flex-col gap-2.5" aria-hidden>
          {Array.from({ length: 2 }, (_, index) => (
            <Skeleton key={index} className="h-24 w-full rounded-xl" />
          ))}
        </div>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Skills">
        <ErrorState
          title="Couldn't load your skills"
          error={query.error}
          onRetry={() => query.refetch()}
          isRetrying={query.isRefetching}
        />
      </Card>
    );
  }

  if (categoriesQuery.isError || servicesQuery.isError) {
    return (
      <Card title="Skills">
        <ErrorState
          title="Couldn't load the service catalog"
          error={categoriesQuery.error ?? servicesQuery.error}
          onRetry={() => {
            categoriesQuery.refetch();
            servicesQuery.refetch();
          }}
          isRetrying={categoriesQuery.isRefetching || servicesQuery.isRefetching}
        />
      </Card>
    );
  }

  const categoryOptions = categoriesQuery.data.map((category) => ({
    value: category.id,
    label: category.name,
  }));

  // A row with no category is dropped silently on save, which reads as the
  // save having failed. Block the save and say so instead.
  const incompleteCount = rows.filter((row) => row.categoryId.trim() === "").length;

  return (
    <Card
      title="Skills"
      description="Service categories and services you're qualified to fulfil."
      actions={
        rows.length > 0 ? (
          <Button variant="secondary" size="sm" onClick={addRow}>
            Add skill
          </Button>
        ) : null
      }
    >
      <div className="flex flex-col gap-4">
        {mutation.isError ? (
          <ErrorState title="Couldn't save your skills" error={mutation.error} />
        ) : null}

        {rows.length === 0 ? (
          <EmptyState
            title="No skills added yet"
            description="Tell us what you can do — you'll only be matched to jobs in the categories you list here."
            action={
              <Button variant="secondary" onClick={addRow}>
                Add your first skill
              </Button>
            }
          />
        ) : (
          <div className="flex flex-col gap-2.5">
            {rows.map((row, index) => {
              const serviceOptions = [
                { value: NO_SPECIFIC_SERVICE, label: "Any service in this category" },
                ...servicesQuery.data
                  .filter((service) => service.categoryId === row.categoryId)
                  .map((service) => ({ value: service.id, label: service.name })),
              ];

              return (
                <div
                  key={index}
                  className="rounded-xl border border-line bg-surface-2 p-3.5"
                >
                  <div className="flex items-start justify-between gap-3">
                    <p className="text-xs font-semibold uppercase tracking-wide text-fg-muted">
                      Skill {index + 1}
                    </p>
                    <IconButton
                      label={`Remove skill ${index + 1}`}
                      onClick={() => removeRow(index)}
                      className="-mr-1.5 -mt-1.5 shrink-0"
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

                  <div className="mt-2 grid gap-3 sm:grid-cols-2">
                    <Select
                      id={`skill-category-${index}`}
                      label="Category"
                      placeholder="Select a category…"
                      options={categoryOptions}
                      value={row.categoryId}
                      onChange={(e) =>
                        updateRow(index, { categoryId: e.target.value, serviceId: "" })
                      }
                    />
                    <Select
                      id={`skill-service-${index}`}
                      label="Service"
                      hint={!row.categoryId ? "Pick a category first." : undefined}
                      options={serviceOptions}
                      value={row.serviceId ?? NO_SPECIFIC_SERVICE}
                      disabled={!row.categoryId}
                      onChange={(e) => updateRow(index, { serviceId: e.target.value })}
                    />
                  </div>
                </div>
              );
            })}
          </div>
        )}

        {incompleteCount > 0 ? (
          <Alert tone="warning">
            {incompleteCount === 1
              ? "One skill has no category selected. Choose one, or remove the row, to save."
              : `${incompleteCount} skills have no category selected. Choose them, or remove the rows, to save.`}
          </Alert>
        ) : null}

        <div className="flex items-center justify-between gap-3 border-t border-line pt-4">
          <p className="text-sm text-fg-muted" aria-live="polite">
            {isDirty ? "You have unsaved changes." : "All changes saved."}
          </p>
          <Button
            type="button"
            loading={mutation.isPending}
            disabled={!isDirty || incompleteCount > 0}
            onClick={save}
          >
            Save changes
          </Button>
        </div>
      </div>
    </Card>
  );
}
