"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Alert, Button, CheckboxField, Field, Modal, PageHeading, Select, Textarea } from "@/components/ui";
import { FormGrid, SearchableSelect, formatCurrency } from "@/components/data-table";
import { EntityTable } from "@/components/entity-table";
import { describeError } from "@/lib/api";
import { createService, listCategories, listServiceGroups, listServices, setServiceActive } from "@/lib/catalog-api";
import type { ServiceAdminResponse } from "@/lib/catalog-types";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { CatalogTabs } from "../_components/CatalogTabs";

const slugPattern = /^[a-z0-9]+(-[a-z0-9]+)*$/;

const serviceSchema = z.object({
  categoryId: z.string().min(1, "Select a category"),
  name: z.string().min(1, "Service name is required").max(200),
  slug: z.string().min(1, "Slug is required").max(200).regex(slugPattern, "Lowercase letters, numbers and hyphens only"),
  shortDescription: z.string().max(500).optional().or(z.literal("")),
  description: z.string().max(2000),
  price: z.number().positive("Price must be greater than 0"),
  coverImageUrl: z.string().max(500).optional().or(z.literal("")),
  durationMinutes: z.number().int().positive("Duration must be greater than 0"),
  inclusions: z.string().max(4000),
  exclusions: z.string().max(4000),
  sortOrder: z.number().int().min(0),
  serviceGroupId: z.string().optional().or(z.literal("")),
  pricingType: z.enum(["Fixed", "Variable"]),
  isTaxApplicable: z.boolean(),
  isAddOnAllowed: z.boolean(),
  isQuantityAllowed: z.boolean(),
  isInspectionBased: z.boolean(),
  isSlotRequired: z.boolean(),
  isAddressRequired: z.boolean(),
  isCustomerNoteAllowed: z.boolean(),
});
type ServiceFormValues = z.infer<typeof serviceSchema>;

/**
 * Admin service/package management screen (SRS 12.6, tasks 105-106): create
 * services under a category with the full field set and option flags.
 * Editing an existing service (including its gallery) happens on its own
 * page (`/catalog/services/[id]`).
 */
export default function CatalogServicesPage() {
  const claims = useAdminClaims();
  const [categoryFilter, setCategoryFilter] = useState("");
  const [addOpen, setAddOpen] = useState(false);
  const queryClient = useQueryClient();

  const canWrite = canWriteModule(claims, "catalog");

  const categoriesQuery = useQuery({ queryKey: ["categories"], queryFn: listCategories });
  const servicesQuery = useQuery({
    queryKey: ["services", categoryFilter],
    queryFn: () => listServices(categoryFilter || undefined),
  });

  const categoryOptions = (categoriesQuery.data ?? []).map((c) => ({ value: c.id, label: c.name }));

  const form = useForm<ServiceFormValues>({
    resolver: zodResolver(serviceSchema),
    defaultValues: {
      categoryId: "",
      name: "",
      slug: "",
      shortDescription: "",
      description: "",
      price: 0,
      coverImageUrl: "",
      durationMinutes: 60,
      inclusions: "",
      exclusions: "",
      sortOrder: 0,
      serviceGroupId: "",
      pricingType: "Fixed",
      isTaxApplicable: true,
      isAddOnAllowed: true,
      isQuantityAllowed: false,
      isInspectionBased: false,
      isSlotRequired: true,
      isAddressRequired: true,
      isCustomerNoteAllowed: true,
    },
  });

  const selectedCategoryId = form.watch("categoryId");
  const serviceGroupsQuery = useQuery({
    queryKey: ["service-groups", selectedCategoryId],
    queryFn: () => listServiceGroups(selectedCategoryId),
    enabled: !!selectedCategoryId,
  });
  const serviceGroupOptions = [
    { value: "", label: "No group" },
    ...(serviceGroupsQuery.data ?? []).map((g) => ({ value: g.id, label: g.name })),
  ];

  const createMutation = useMutation({
    mutationFn: createService,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["services"] });
      form.reset({ ...form.getValues(), name: "", slug: "", description: "", shortDescription: "" });
      setAddOpen(false);
    },
  });

  const toggleMutation = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) => setServiceActive(id, isActive),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["services"] }),
  });

  const onSubmit = form.handleSubmit((values) =>
    createMutation.mutate({
      categoryId: values.categoryId,
      name: values.name,
      slug: values.slug,
      description: values.description,
      shortDescription: values.shortDescription || null,
      price: values.price,
      coverImageUrl: values.coverImageUrl || null,
      inclusions: values.inclusions,
      exclusions: values.exclusions,
      cancellationPolicy: null,
      reschedulePolicy: null,
      durationMinutes: values.durationMinutes,
      sortOrder: values.sortOrder,
      serviceGroupId: values.serviceGroupId || null,
      seoTitle: null,
      seoMetaDescription: null,
      pricingType: values.pricingType,
      isTaxApplicable: values.isTaxApplicable,
      isAddOnAllowed: values.isAddOnAllowed,
      isQuantityAllowed: values.isQuantityAllowed,
      isInspectionBased: values.isInspectionBased,
      isSlotRequired: values.isSlotRequired,
      isAddressRequired: values.isAddressRequired,
      isCustomerNoteAllowed: values.isCustomerNoteAllowed,
    }),
  );

  return (
    <div className="mx-auto flex w-full max-w-5xl flex-col gap-6">
      <div>
        <PageHeading title="Catalog" subtitle="Categories, services and add-ons (SRS 12.5-12.7)." />
        <CatalogTabs />
      </div>

      <EntityTable<ServiceAdminResponse>
        title="Services"
        description="Packages offered under a category, with duration, pricing and booking options (SRS 12.6)."
        actions={
          <div className="flex flex-wrap items-end gap-2">
            <div className="w-56">
              <Select
                label="Filter by category"
                value={categoryFilter}
                onChange={(e) => setCategoryFilter(e.target.value)}
                options={[{ value: "", label: "All categories" }, ...categoryOptions]}
              />
            </div>
            {canWrite ? (
              <Button type="button" onClick={() => setAddOpen(true)}>
                Add service
              </Button>
            ) : null}
          </div>
        }
        items={servicesQuery.data}
        isLoading={servicesQuery.isPending}
        isFetching={servicesQuery.isFetching}
        error={servicesQuery.error}
        onRetry={() => servicesQuery.refetch()}
        emptyMessage={categoryFilter ? "No services in this category" : "No services yet"}
        emptyAction={
          categoryFilter ? (
            <Button variant="secondary" onClick={() => setCategoryFilter("")}>
              Show all categories
            </Button>
          ) : undefined
        }
        canWrite={canWrite}
        entityLabel="service"
        labelOf={(service) => service.name}
        minWidth="960px"
        togglingId={toggleMutation.isPending ? toggleMutation.variables?.id : undefined}
        toggleError={toggleMutation.error}
        onToggleActive={(service) => toggleMutation.mutate({ id: service.id, isActive: !service.isActive })}
        columns={[
          {
            header: "Name",
            sortValue: (service) => service.name,
            render: (service) => (
              <Link
                href={`/catalog/services/${service.id}`}
                className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
              >
                {service.name}
              </Link>
            ),
          },
          {
            header: "Category",
            sortValue: (service) => service.categoryName,
            render: (service) => service.categoryName,
          },
          {
            header: "Price",
            numeric: true,
            sortValue: (service) => service.price,
            render: (service) => formatCurrency(service.price),
          },
          {
            header: "Duration",
            numeric: true,
            sortValue: (service) => service.durationMinutes,
            render: (service) => `${service.durationMinutes} min`,
          },
          {
            header: "Pricing",
            sortValue: (service) => service.pricingType,
            render: (service) => service.pricingType,
          },
          {
            header: "Featured",
            sortValue: (service) => service.isFeatured,
            render: (service) => (service.isFeatured ? "Yes" : "No"),
          },
        ]}
      />

      {canWrite ? (
        <Modal
          open={addOpen}
          onClose={() => setAddOpen(false)}
          title="Add service"
          description="Creates the service immediately; it is bookable once activated and priced."
          size="lg"
          footer={
            <>
              <Button type="button" variant="secondary" onClick={() => setAddOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" form="add-service-form" loading={form.formState.isSubmitting || createMutation.isPending}>
                Add service
              </Button>
            </>
          }
        >
          <form id="add-service-form" onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
            {createMutation.isError ? <Alert>{describeError(createMutation.error)}</Alert> : null}

            <FormGrid>
              <SearchableSelect
                label="Category"
                required
                placeholder="Search categories…"
                error={form.formState.errors.categoryId?.message}
                options={categoryOptions}
                value={form.watch("categoryId")}
                onChange={(value) => form.setValue("categoryId", value, { shouldValidate: true })}
              />
              <Field label="Name" required error={form.formState.errors.name?.message} {...form.register("name")} />
            </FormGrid>

            <FormGrid columns={3}>
              <Field
                label="Slug"
                required
                hint="Lowercase letters, numbers and hyphens."
                error={form.formState.errors.slug?.message}
                {...form.register("slug")}
              />
              <Field
                label="Price"
                type="number"
                step="0.01"
                required
                leading="₹"
                error={form.formState.errors.price?.message}
                {...form.register("price", { valueAsNumber: true })}
              />
              <Field
                label="Duration (minutes)"
                type="number"
                required
                error={form.formState.errors.durationMinutes?.message}
                {...form.register("durationMinutes", { valueAsNumber: true })}
              />
            </FormGrid>

            <Field
              label="Short description"
              error={form.formState.errors.shortDescription?.message}
              {...form.register("shortDescription")}
            />
            <Field
              label="Cover image URL"
              hint="Shown on customer-facing listing cards. Leave blank to use a graphic placeholder."
              error={form.formState.errors.coverImageUrl?.message}
              {...form.register("coverImageUrl")}
            />
            <Textarea label="Description" error={form.formState.errors.description?.message} {...form.register("description")} />
            <FormGrid>
              <Textarea label="Inclusions" error={form.formState.errors.inclusions?.message} {...form.register("inclusions")} />
              <Textarea label="Exclusions" error={form.formState.errors.exclusions?.message} {...form.register("exclusions")} />
            </FormGrid>

            <FormGrid>
              <Field
                label="Sort order"
                type="number"
                error={form.formState.errors.sortOrder?.message}
                {...form.register("sortOrder", { valueAsNumber: true })}
              />
              <Select
                label="Pricing type"
                options={[
                  { value: "Fixed", label: "Fixed package price" },
                  { value: "Variable", label: "Variable (base + add-ons)" },
                ]}
                {...form.register("pricingType")}
              />
            </FormGrid>

            <Select
              label="Service group"
              hint="Optional section header shown on the customer-facing listing (e.g. &ldquo;Repair &amp; gas refill&rdquo;). Leave as No group to show this service directly under the category."
              disabled={!selectedCategoryId}
              options={serviceGroupOptions}
              {...form.register("serviceGroupId")}
            />

            <fieldset className="grid grid-cols-1 gap-2 sm:grid-cols-2">
              <legend className="mb-2 text-sm font-medium text-fg">Booking options</legend>
              <CheckboxField
                label="Tax applicable"
                checked={form.watch("isTaxApplicable")}
                onChange={(v) => form.setValue("isTaxApplicable", v)}
              />
              <CheckboxField
                label="Add-ons allowed"
                checked={form.watch("isAddOnAllowed")}
                onChange={(v) => form.setValue("isAddOnAllowed", v)}
              />
              <CheckboxField
                label="Quantity allowed"
                checked={form.watch("isQuantityAllowed")}
                onChange={(v) => form.setValue("isQuantityAllowed", v)}
              />
              <CheckboxField
                label="Inspection required before scheduling"
                checked={form.watch("isInspectionBased")}
                onChange={(v) => form.setValue("isInspectionBased", v)}
              />
              <CheckboxField
                label="Slot required"
                checked={form.watch("isSlotRequired")}
                onChange={(v) => form.setValue("isSlotRequired", v)}
              />
              <CheckboxField
                label="Address required"
                checked={form.watch("isAddressRequired")}
                onChange={(v) => form.setValue("isAddressRequired", v)}
              />
              <CheckboxField
                label="Customer note allowed"
                checked={form.watch("isCustomerNoteAllowed")}
                onChange={(v) => form.setValue("isCustomerNoteAllowed", v)}
              />
            </fieldset>
          </form>
        </Modal>
      ) : null}
    </div>
  );
}
