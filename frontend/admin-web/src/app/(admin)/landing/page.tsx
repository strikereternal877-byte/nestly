"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { formatCurrency } from "@/components/data-table";
import { SectionError } from "@/components/screen-states";
import { Alert, Button, Card, PageHeading, Select, Skeleton, Tabs, cx, useToast } from "@/components/ui";
import { describeError } from "@/lib/api";
import { listCategories, listServices } from "@/lib/catalog-api";
import type { CategoryResponse, ServiceAdminResponse } from "@/lib/catalog-types";
import {
  getLandingConfig,
  updateCategorySection,
  updateMostBooked,
  updateNewAndTrending,
} from "@/lib/landing-api";
import { MAX_SERVICES_PER_CATEGORY_SECTION } from "@/lib/landing-types";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";

type TabKey = "new-and-trending" | "most-booked" | "category-sections";

const TABS: readonly { value: TabKey; label: string }[] = [
  { value: "new-and-trending", label: "New & Trending" },
  { value: "most-booked", label: "Most Booked Services" },
  { value: "category-sections", label: "Category sections" },
];

/**
 * Landing-page curation (customer home page). Three independent sections,
 * each saved wholesale: the list the admin arranges here IS the display
 * order, so nothing tracks sort values by hand.
 *
 * This screen only *selects* existing catalog entries - it never creates or
 * edits them, which is why it sits under the "cms" permission module
 * alongside banners rather than under "catalog".
 */
export default function LandingPage() {
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "cms");
  const queryClient = useQueryClient();
  const pushToast = useToast();

  const [tab, setTab] = useState<TabKey>("new-and-trending");

  const configQuery = useQuery({ queryKey: ["admin-landing-config"], queryFn: getLandingConfig });
  const categoriesQuery = useQuery({
    queryKey: ["catalog-categories"],
    queryFn: listCategories,
    staleTime: 5 * 60 * 1000,
  });
  const servicesQuery = useQuery({
    queryKey: ["catalog-services"],
    queryFn: () => listServices(),
    staleTime: 5 * 60 * 1000,
  });

  const onSaved = (message: string) => {
    queryClient.invalidateQueries({ queryKey: ["admin-landing-config"] });
    pushToast("success", message);
  };
  const onSaveError = (error: unknown) => pushToast("error", describeError(error));

  const newAndTrendingMutation = useMutation({
    mutationFn: (categoryIds: string[]) => updateNewAndTrending({ categoryIds }),
    onSuccess: () => onSaved("New & Trending updated."),
    onError: onSaveError,
  });

  const mostBookedMutation = useMutation({
    mutationFn: (serviceIds: string[]) => updateMostBooked({ serviceIds }),
    onSuccess: () => onSaved("Most Booked Services updated."),
    onError: onSaveError,
  });

  const categorySectionMutation = useMutation({
    mutationFn: ({ categoryId, serviceIds }: { categoryId: string; serviceIds: string[] }) =>
      updateCategorySection(categoryId, { serviceIds }),
    onSuccess: () => onSaved("Category section updated."),
    onError: onSaveError,
  });

  const isLoading = configQuery.isPending || categoriesQuery.isPending || servicesQuery.isPending;
  const error = configQuery.error ?? categoriesQuery.error ?? servicesQuery.error;

  return (
    <div className="w-full max-w-7xl">
      <PageHeading
        title="Landing page"
        subtitle="Choose which categories and services the customer home page features. Selection order is display order."
      />

      <Tabs tabs={TABS} value={tab} onChange={setTab} label="Landing page sections" className="mb-6" />

      {!canWrite ? (
        <div className="mb-6">
          <Alert tone="info">Read-only — you do not hold CMS write access.</Alert>
        </div>
      ) : null}

      {isLoading ? (
        <Card>
          <Skeleton className="h-5 w-48" />
          <Skeleton className="mt-4 h-4 w-full" />
          <Skeleton className="mt-2 h-4 w-3/4" />
        </Card>
      ) : error ? (
        <SectionError error={error} onRetry={() => configQuery.refetch()} />
      ) : tab === "new-and-trending" ? (
        <NewAndTrendingTab
          categories={categoriesQuery.data ?? []}
          initialIds={(configQuery.data?.newAndTrending ?? []).map((item) => item.categoryId)}
          canWrite={canWrite}
          busy={newAndTrendingMutation.isPending}
          onSave={(ids) => newAndTrendingMutation.mutate(ids)}
        />
      ) : tab === "most-booked" ? (
        <MostBookedTab
          services={servicesQuery.data ?? []}
          initialIds={(configQuery.data?.mostBooked ?? []).map((item) => item.serviceId)}
          canWrite={canWrite}
          busy={mostBookedMutation.isPending}
          onSave={(ids) => mostBookedMutation.mutate(ids)}
        />
      ) : (
        <CategorySectionsTab
          categories={categoriesQuery.data ?? []}
          services={servicesQuery.data ?? []}
          configured={configQuery.data?.categorySections ?? []}
          canWrite={canWrite}
          busy={categorySectionMutation.isPending}
          onSave={(categoryId, serviceIds) => categorySectionMutation.mutate({ categoryId, serviceIds })}
        />
      )}
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Shared picker                                                              */
/* -------------------------------------------------------------------------- */

interface PickerOption {
  id: string;
  label: string;
  /** Secondary line — the parent category, or the price. */
  hint?: string;
}

/**
 * Two-pane picker: everything available on the left, the ordered selection on
 * the right. Order is explicit (move up/down) because on a landing page the
 * first card is the one most customers see.
 */
function SelectionPicker({
  options,
  selectedIds,
  onChange,
  canWrite,
  max,
  emptyLabel,
}: {
  options: PickerOption[];
  selectedIds: string[];
  onChange: (ids: string[]) => void;
  canWrite: boolean;
  max?: number;
  emptyLabel: string;
}) {
  const byId = useMemo(() => new Map(options.map((option) => [option.id, option])), [options]);
  const atLimit = max !== undefined && selectedIds.length >= max;

  const toggle = (id: string) => {
    if (!canWrite) return;
    if (selectedIds.includes(id)) {
      onChange(selectedIds.filter((selected) => selected !== id));
    } else if (!atLimit) {
      onChange([...selectedIds, id]);
    }
  };

  const move = (index: number, delta: number) => {
    const target = index + delta;
    if (target < 0 || target >= selectedIds.length) return;
    const next = [...selectedIds];
    [next[index], next[target]] = [next[target], next[index]];
    onChange(next);
  };

  return (
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
      <div>
        <p className="mb-2 text-sm font-semibold text-fg">
          Available{max !== undefined ? ` (pick up to ${max})` : ""}
        </p>
        <div className="max-h-96 overflow-y-auto rounded-sm border border-line">
          {options.length === 0 ? (
            <p className="px-4 py-6 text-center text-sm text-fg-muted">{emptyLabel}</p>
          ) : (
            options.map((option) => {
              const isSelected = selectedIds.includes(option.id);
              return (
                <button
                  key={option.id}
                  type="button"
                  onClick={() => toggle(option.id)}
                  disabled={!canWrite || (!isSelected && atLimit)}
                  className={cx(
                    "flex w-full items-center gap-3 border-b border-line px-4 py-2.5 text-left transition-colors duration-fast ease-out last:border-b-0",
                    isSelected ? "bg-brand-50 dark:bg-brand-500/10" : "hover:bg-surface-2",
                    (!canWrite || (!isSelected && atLimit)) && "cursor-not-allowed opacity-55",
                  )}
                >
                  <span
                    aria-hidden
                    className={cx(
                      "flex h-4 w-4 shrink-0 items-center justify-center rounded border",
                      isSelected ? "border-brand-600 bg-brand-600 text-white" : "border-line-strong",
                    )}
                  >
                    {isSelected ? "✓" : ""}
                  </span>
                  <span className="min-w-0">
                    <span className="block truncate text-sm text-fg">{option.label}</span>
                    {option.hint ? (
                      <span className="block truncate text-xs text-fg-muted">{option.hint}</span>
                    ) : null}
                  </span>
                </button>
              );
            })
          )}
        </div>
      </div>

      <div>
        <p className="mb-2 text-sm font-semibold text-fg">
          Selected <span className="nums text-fg-muted">({selectedIds.length})</span>
        </p>
        <div className="max-h-96 overflow-y-auto rounded-sm border border-line">
          {selectedIds.length === 0 ? (
            <p className="px-4 py-6 text-center text-sm text-fg-muted">Nothing selected yet.</p>
          ) : (
            selectedIds.map((id, index) => (
              <div
                key={id}
                className="flex items-center gap-3 border-b border-line px-4 py-2.5 last:border-b-0"
              >
                <span className="nums w-5 shrink-0 text-xs text-fg-subtle">{index + 1}</span>
                <span className="min-w-0 flex-1 truncate text-sm text-fg">
                  {byId.get(id)?.label ?? "(no longer in catalog)"}
                </span>
                {canWrite ? (
                  <span className="flex shrink-0 items-center gap-1">
                    <Button size="sm" variant="ghost" onClick={() => move(index, -1)} disabled={index === 0}>
                      ↑
                    </Button>
                    <Button
                      size="sm"
                      variant="ghost"
                      onClick={() => move(index, 1)}
                      disabled={index === selectedIds.length - 1}
                    >
                      ↓
                    </Button>
                    <Button size="sm" variant="ghost" onClick={() => toggle(id)}>
                      Remove
                    </Button>
                  </span>
                ) : null}
              </div>
            ))
          )}
        </div>
      </div>
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Tabs                                                                       */
/* -------------------------------------------------------------------------- */

function NewAndTrendingTab({
  categories,
  initialIds,
  canWrite,
  busy,
  onSave,
}: {
  categories: CategoryResponse[];
  initialIds: string[];
  canWrite: boolean;
  busy: boolean;
  onSave: (ids: string[]) => void;
}) {
  const [selected, setSelected] = useState<string[]>(initialIds);
  // Adjusting state when a prop changes (react.dev/learn/you-might-not-need-an-effect).
  const initialKey = initialIds.join(",");
  const [seededInitialKey, setSeededInitialKey] = useState(initialKey);
  if (initialKey !== seededInitialKey) {
    setSeededInitialKey(initialKey);
    setSelected(initialIds);
  }

  // Sub-categories first (that is what this section is for), each labelled
  // with its parent so the admin reads it as "Category → Sub-category".
  const parentNames = useMemo(
    () => new Map(categories.map((category) => [category.id, category.name])),
    [categories],
  );
  const options: PickerOption[] = useMemo(
    () =>
      categories
        .filter((category) => category.isActive)
        .map((category) => ({
          id: category.id,
          label: category.name,
          hint: category.parentCategoryId
            ? `${parentNames.get(category.parentCategoryId) ?? "—"} → ${category.name}`
            : "Top-level category",
        }))
        .sort((a, b) => (a.hint ?? "").localeCompare(b.hint ?? "")),
    [categories, parentNames],
  );

  return (
    <Card
      title="New & Trending"
      description="Sub-category cards shown under the hero. Images come from each category's card image; no prices are shown here."
      footer={
        canWrite ? (
          <Button loading={busy} onClick={() => onSave(selected)}>
            Save New & Trending
          </Button>
        ) : null
      }
    >
      <SelectionPicker
        options={options}
        selectedIds={selected}
        onChange={setSelected}
        canWrite={canWrite}
        emptyLabel="No active categories yet."
      />
    </Card>
  );
}

function MostBookedTab({
  services,
  initialIds,
  canWrite,
  busy,
  onSave,
}: {
  services: ServiceAdminResponse[];
  initialIds: string[];
  canWrite: boolean;
  busy: boolean;
  onSave: (ids: string[]) => void;
}) {
  const [selected, setSelected] = useState<string[]>(initialIds);
  // Adjusting state when a prop changes (react.dev/learn/you-might-not-need-an-effect).
  const initialKey = initialIds.join(",");
  const [seededInitialKey, setSeededInitialKey] = useState(initialKey);
  if (initialKey !== seededInitialKey) {
    setSeededInitialKey(initialKey);
    setSelected(initialIds);
  }

  const options: PickerOption[] = useMemo(
    () =>
      services
        .filter((service) => service.isActive)
        .map((service) => ({
          id: service.id,
          label: service.name,
          hint: `${service.categoryName} · ${formatCurrency(service.price)}`,
        }))
        .sort((a, b) => a.label.localeCompare(b.label)),
    [services],
  );

  return (
    <Card
      title="Most Booked Services"
      description="Bookable services shown with image, title and price, immediately after New & Trending."
      footer={
        canWrite ? (
          <Button loading={busy} onClick={() => onSave(selected)}>
            Save Most Booked
          </Button>
        ) : null
      }
    >
      <SelectionPicker
        options={options}
        selectedIds={selected}
        onChange={setSelected}
        canWrite={canWrite}
        emptyLabel="No active services yet."
      />
    </Card>
  );
}

function CategorySectionsTab({
  categories,
  services,
  configured,
  canWrite,
  busy,
  onSave,
}: {
  categories: CategoryResponse[];
  services: ServiceAdminResponse[];
  configured: { categoryId: string; categoryName: string; services: { serviceId: string }[] }[];
  canWrite: boolean;
  busy: boolean;
  onSave: (categoryId: string, serviceIds: string[]) => void;
}) {
  // Only categories that actually have services can head a strip.
  const eligible = useMemo(() => {
    const withServices = new Set(services.filter((s) => s.isActive).map((s) => s.categoryId));
    return categories.filter((category) => category.isActive && withServices.has(category.id));
  }, [categories, services]);

  const [categoryId, setCategoryId] = useState("");
  useEffect(() => {
    // Picking an initial default once the eligible list arrives, not a
    // render-time prop change; self-disables once categoryId is set.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    if (!categoryId && eligible.length > 0) setCategoryId(eligible[0].id);
  }, [eligible, categoryId]);

  const configuredForCategory = useMemo(
    () => configured.find((section) => section.categoryId === categoryId)?.services.map((s) => s.serviceId) ?? [],
    [configured, categoryId],
  );

  const [selected, setSelected] = useState<string[]>([]);
  // Adjusting state when a prop changes (react.dev/learn/you-might-not-need-an-effect).
  const configuredKey = `${categoryId}:${configuredForCategory.join(",")}`;
  const [seededConfiguredKey, setSeededConfiguredKey] = useState(configuredKey);
  if (configuredKey !== seededConfiguredKey) {
    setSeededConfiguredKey(configuredKey);
    setSelected(configuredForCategory);
  }

  const options: PickerOption[] = useMemo(
    () =>
      services
        .filter((service) => service.isActive && service.categoryId === categoryId)
        .map((service) => ({
          id: service.id,
          label: service.name,
          hint: formatCurrency(service.price),
        }))
        .sort((a, b) => a.label.localeCompare(b.label)),
    [services, categoryId],
  );

  return (
    <div className="flex flex-col gap-6">
      <Card
        title="Category sections"
        description={`One strip per category, headed by the category name, showing up to ${MAX_SERVICES_PER_CATEGORY_SECTION} services. Services must belong to the category heading them.`}
        footer={
          canWrite && categoryId ? (
            <Button loading={busy} onClick={() => onSave(categoryId, selected)}>
              Save this section
            </Button>
          ) : null
        }
      >
        {eligible.length === 0 ? (
          <Alert tone="info">No active category has services yet, so there is nothing to feature.</Alert>
        ) : (
          <div className="flex flex-col gap-5">
            <div className="max-w-md">
              <Select
                label="Category heading"
                options={eligible.map((category) => ({ value: category.id, label: category.name }))}
                value={categoryId}
                onChange={(event) => setCategoryId(event.target.value)}
              />
            </div>

            <SelectionPicker
              options={options}
              selectedIds={selected}
              onChange={setSelected}
              canWrite={canWrite}
              max={MAX_SERVICES_PER_CATEGORY_SECTION}
              emptyLabel="This category has no active services."
            />
          </div>
        )}
      </Card>

      {configured.length > 0 ? (
        <Card title="Configured sections" description="Every category strip currently shown on the home page, in page order.">
          <ul className="flex flex-col gap-2">
            {configured.map((section) => (
              <li key={section.categoryId} className="flex items-center justify-between gap-3 text-sm">
                <span className="text-fg">{section.categoryName}</span>
                <span className="flex items-center gap-3">
                  <span className="nums text-fg-muted">
                    {section.services.length} service{section.services.length === 1 ? "" : "s"}
                  </span>
                  <Button size="sm" variant="ghost" onClick={() => setCategoryId(section.categoryId)}>
                    Edit
                  </Button>
                </span>
              </li>
            ))}
          </ul>
        </Card>
      ) : null}
    </div>
  );
}
