"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Alert, Button, Card, Select } from "@/components/ui";
import { describeError } from "@/lib/api";
import { listCatalogCategories, listCatalogServices } from "@/lib/lookup-api";
import { getSkills, updateSkills } from "@/lib/profile-api";
import type { ProviderSkillInput } from "@/lib/profile-types";

const EMPTY_ROW: ProviderSkillInput = { categoryId: "", serviceId: "" };
const NO_SPECIFIC_SERVICE = "";

/**
 * Skills editor (docs/PROVIDER.md's Capability & Coverage domain,
 * `provider_skill_mapping`): the categories/services a provider is qualified
 * to fulfil. Same full-replace PUT shape as ServiceAreasSection. Category/
 * service are chosen from real name dropdowns (task 205, backed by
 * `/api/v1/catalog/{categories,services}`) rather than the hand-typed GUIDs
 * this screen originally shipped with - there was no catalog lookup
 * endpoint in provider-api's contract at the time.
 */
export function SkillsSection() {
  const queryClient = useQueryClient();
  const [rows, setRows] = useState<ProviderSkillInput[]>([]);
  const [isDirty, setIsDirty] = useState(false);

  const query = useQuery({ queryKey: ["provider-skills"], queryFn: getSkills });
  const categoriesQuery = useQuery({ queryKey: ["catalog-categories"], queryFn: listCatalogCategories });
  // Fetched once, unfiltered, then filtered per-row client-side - simpler than
  // one query per row and the catalog is small enough that this is cheap.
  const servicesQuery = useQuery({ queryKey: ["catalog-services"], queryFn: () => listCatalogServices() });

  useEffect(() => {
    if (query.data && !isDirty) {
      setRows(
        query.data.map((skill) => ({
          categoryId: skill.categoryId,
          serviceId: skill.serviceId ?? "",
        })),
      );
    }
  }, [query.data, isDirty]);

  const mutation = useMutation({
    mutationFn: (skills: ProviderSkillInput[]) => updateSkills({ skills }),
    onSuccess: (skills) => {
      queryClient.setQueryData(["provider-skills"], skills);
      setIsDirty(false);
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
        <p className="text-sm text-neutral-500">Loading skills…</p>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Skills">
        <Alert>{describeError(query.error)}</Alert>
      </Card>
    );
  }

  if (categoriesQuery.isError || servicesQuery.isError) {
    return (
      <Card title="Skills">
        <Alert>{describeError(categoriesQuery.error ?? servicesQuery.error)}</Alert>
      </Card>
    );
  }

  const categoryOptions = categoriesQuery.data.map((category) => ({ value: category.id, label: category.name }));

  return (
    <Card title="Skills" description="Service categories and services you're qualified to fulfil.">
      {mutation.isError ? (
        <div className="mb-3">
          <Alert>{describeError(mutation.error)}</Alert>
        </div>
      ) : null}

      {rows.length === 0 ? (
        <p className="mb-3 text-sm text-neutral-600 dark:text-neutral-400">No skills added yet.</p>
      ) : (
        <div className="flex flex-col gap-3">
          {rows.map((row, index) => {
            const serviceOptions = [
              { value: NO_SPECIFIC_SERVICE, label: "Any service in this category" },
              ...servicesQuery.data
                .filter((service) => service.categoryId === row.categoryId)
                .map((service) => ({ value: service.id, label: service.name })),
            ];

            return (
              <div key={index} className="flex flex-wrap items-end gap-3">
                <div className="w-56">
                  <Select
                    id={`skill-category-${index}`}
                    label="Category"
                    placeholder="Select a category…"
                    options={categoryOptions}
                    value={row.categoryId}
                    onChange={(e) => updateRow(index, { categoryId: e.target.value, serviceId: "" })}
                  />
                </div>
                <div className="w-56">
                  <Select
                    id={`skill-service-${index}`}
                    label="Service"
                    options={serviceOptions}
                    value={row.serviceId ?? NO_SPECIFIC_SERVICE}
                    disabled={!row.categoryId}
                    onChange={(e) => updateRow(index, { serviceId: e.target.value })}
                  />
                </div>
                <Button type="button" variant="danger" onClick={() => removeRow(index)}>
                  Remove
                </Button>
              </div>
            );
          })}
        </div>
      )}

      <div className="mt-4 flex gap-2">
        <Button type="button" variant="secondary" onClick={addRow}>
          Add skill
        </Button>
        <Button type="button" disabled={mutation.isPending || !isDirty} onClick={save}>
          {mutation.isPending ? "Saving…" : "Save changes"}
        </Button>
      </div>
    </Card>
  );
}
