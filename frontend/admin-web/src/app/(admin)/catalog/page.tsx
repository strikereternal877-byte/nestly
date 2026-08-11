"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useForm } from "react-hook-form";
import { useState } from "react";
import { z } from "zod";
import { Alert, Button, Field, Modal, PageHeading, Textarea } from "@/components/ui";
import { FormGrid, SearchableSelect } from "@/components/data-table";
import { EntityTable } from "@/components/entity-table";
import { describeError } from "@/lib/api";
import { createCategory, listCategories, setCategoryActive } from "@/lib/catalog-api";
import type { CategoryResponse } from "@/lib/catalog-types";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { CatalogTabs } from "./_components/CatalogTabs";

const categorySchema = z.object({
  name: z.string().min(1, "Category name is required").max(200),
  slug: z
    .string()
    .min(1, "Slug is required")
    .max(200)
    .regex(/^[a-z0-9]+(-[a-z0-9]+)*$/, "Lowercase letters, numbers and hyphens only (e.g. \"deep-cleaning\")"),
  description: z.string().max(2000),
  iconUrl: z.string().max(500).optional().or(z.literal("")),
  bannerUrl: z.string().max(500).optional().or(z.literal("")),
  sortOrder: z.number().int().min(0),
  seoTitle: z.string().max(200).optional().or(z.literal("")),
  seoMetaDescription: z.string().max(500).optional().or(z.literal("")),
  parentCategoryId: z.string().optional().or(z.literal("")),
});
type CategoryFormValues = z.infer<typeof categorySchema>;

/**
 * Flattens the category list into parent-then-children display order (Phase
 * 3 catalog redesign) - top-level categories in their own sort order, each
 * immediately followed by its own children. A plain indented list rather
 * than a new tree component, matching DataTable's documented preference
 * against expand-in-place rows.
 */
function toDisplayOrder(categories: CategoryResponse[]): { category: CategoryResponse; depth: number }[] {
  const byParent = new Map<string | null, CategoryResponse[]>();
  for (const category of categories) {
    const key = category.parentCategoryId;
    byParent.set(key, [...(byParent.get(key) ?? []), category]);
  }

  const result: { category: CategoryResponse; depth: number }[] = [];
  const visit = (parentId: string | null, depth: number) => {
    for (const category of byParent.get(parentId) ?? []) {
      result.push({ category, depth });
      visit(category.id, depth + 1);
    }
  };
  visit(null, 0);
  return result;
}

/**
 * Admin category management screen (SRS 12.5, tasks 103a-104): create
 * categories and manage their display order, media and SEO fields. Editing
 * an existing category happens on its own page (`/catalog/categories/[id]`)
 * since a category carries too many fields for an inline row form.
 */
export default function CatalogCategoriesPage() {
  const claims = useAdminClaims();
  const [addOpen, setAddOpen] = useState(false);
  const queryClient = useQueryClient();

  const canWrite = canWriteModule(claims, "catalog");

  const categoriesQuery = useQuery({ queryKey: ["categories"], queryFn: listCategories });
  const categories = categoriesQuery.data ?? [];
  const displayOrder = toDisplayOrder(categories);
  const depthById = new Map(displayOrder.map(({ category, depth }) => [category.id, depth]));
  const parentOptions = [
    { value: "", label: "No parent (top-level)" },
    ...categories.map((c) => ({ value: c.id, label: c.name })),
  ];

  const form = useForm<CategoryFormValues>({
    resolver: zodResolver(categorySchema),
    defaultValues: {
      name: "",
      slug: "",
      description: "",
      iconUrl: "",
      bannerUrl: "",
      sortOrder: 0,
      seoTitle: "",
      seoMetaDescription: "",
      parentCategoryId: "",
    },
  });

  const createMutation = useMutation({
    mutationFn: createCategory,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["categories"] });
      form.reset();
      setAddOpen(false);
    },
  });

  const toggleMutation = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) => setCategoryActive(id, isActive),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["categories"] }),
  });

  const onSubmit = form.handleSubmit((values) =>
    createMutation.mutate({
      name: values.name,
      slug: values.slug,
      description: values.description,
      iconUrl: values.iconUrl || null,
      bannerUrl: values.bannerUrl || null,
      sortOrder: values.sortOrder,
      seoTitle: values.seoTitle || null,
      seoMetaDescription: values.seoMetaDescription || null,
      parentCategoryId: values.parentCategoryId || null,
    }),
  );

  return (
    <div className="mx-auto flex w-full max-w-5xl flex-col gap-6">
      <div>
        <PageHeading title="Catalog" subtitle="Categories, services and add-ons (SRS 12.5-12.7)." />
        <CatalogTabs />
      </div>

      <EntityTable<CategoryResponse>
        title="Categories"
        description="Top-level catalog grouping shown to customers (SRS 12.5.1). Subcategories are indented under their parent (Phase 3)."
        actions={
          canWrite ? (
            <Button type="button" onClick={() => setAddOpen(true)}>
              Add category
            </Button>
          ) : undefined
        }
        items={displayOrder.map((d) => d.category)}
        isLoading={categoriesQuery.isPending}
        isFetching={categoriesQuery.isFetching}
        error={categoriesQuery.error}
        onRetry={() => categoriesQuery.refetch()}
        emptyMessage="No categories yet"
        canWrite={canWrite}
        entityLabel="category"
        labelOf={(category) => category.name}
        minWidth="760px"
        togglingId={toggleMutation.isPending ? toggleMutation.variables?.id : undefined}
        toggleError={toggleMutation.error}
        onToggleActive={(category) => toggleMutation.mutate({ id: category.id, isActive: !category.isActive })}
        columns={[
          {
            header: "Name",
            sortValue: (category) => category.name,
            render: (category) => (
              <Link
                href={`/catalog/categories/${category.id}`}
                className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
                style={{ paddingLeft: `${(depthById.get(category.id) ?? 0) * 1.25}rem` }}
              >
                {(depthById.get(category.id) ?? 0) > 0 ? "↳ " : null}
                {category.name}
              </Link>
            ),
          },
          { header: "Slug", sortValue: (category) => category.slug, render: (category) => category.slug },
          {
            header: "Sort order",
            numeric: true,
            sortValue: (category) => category.sortOrder,
            render: (category) => category.sortOrder,
          },
          {
            header: "Featured",
            sortValue: (category) => category.isFeatured,
            render: (category) => (category.isFeatured ? "Yes" : "No"),
          },
        ]}
      />

      {canWrite ? (
        <Modal
          open={addOpen}
          onClose={() => setAddOpen(false)}
          title="Add category"
          description="Creates the category immediately; it is live once activated."
          size="lg"
          footer={
            <>
              <Button type="button" variant="secondary" onClick={() => setAddOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" form="add-category-form" loading={form.formState.isSubmitting || createMutation.isPending}>
                Add category
              </Button>
            </>
          }
        >
          <form id="add-category-form" onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
            {createMutation.isError ? <Alert>{describeError(createMutation.error)}</Alert> : null}
            <FormGrid>
              <Field label="Name" required error={form.formState.errors.name?.message} {...form.register("name")} />
              <Field
                label="Slug"
                required
                hint="Lowercase letters, numbers and hyphens — this becomes the customer-facing URL."
                error={form.formState.errors.slug?.message}
                {...form.register("slug")}
              />
            </FormGrid>
            <SearchableSelect
              label="Parent category"
              placeholder="Search categories… (leave blank for top-level)"
              options={parentOptions}
              value={form.watch("parentCategoryId") ?? ""}
              onChange={(value) => form.setValue("parentCategoryId", value)}
            />
            <Textarea label="Description" error={form.formState.errors.description?.message} {...form.register("description")} />
            <FormGrid>
              <Field label="Icon URL" error={form.formState.errors.iconUrl?.message} {...form.register("iconUrl")} />
              <Field label="Banner URL" error={form.formState.errors.bannerUrl?.message} {...form.register("bannerUrl")} />
            </FormGrid>
            <FormGrid columns={3}>
              <Field
                label="Sort order"
                type="number"
                error={form.formState.errors.sortOrder?.message}
                {...form.register("sortOrder", { valueAsNumber: true })}
              />
              <Field label="SEO title" error={form.formState.errors.seoTitle?.message} {...form.register("seoTitle")} />
              <Field
                label="SEO meta description"
                error={form.formState.errors.seoMetaDescription?.message}
                {...form.register("seoMetaDescription")}
              />
            </FormGrid>
          </form>
        </Modal>
      ) : null}
    </div>
  );
}
