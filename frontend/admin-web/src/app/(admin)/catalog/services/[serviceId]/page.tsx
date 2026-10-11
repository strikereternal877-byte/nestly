"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useParams } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import {
  Alert,
  Badge,
  Button,
  Card,
  CheckboxField,
  EmptyState,
  Field,
  PageHeading,
  Select,
  SkeletonText,
  Textarea,
} from "@/components/ui";
import { Breadcrumbs, ConfirmDialog, FormActions, FormGrid, formatCurrency } from "@/components/data-table";
import { StatusBadge } from "@/components/entity-table";
import { DetailError, DetailSkeleton, SectionError } from "@/components/screen-states";
import { describeError } from "@/lib/api";
import {
  addServiceMedia,
  createServiceVariant,
  deleteServiceVariant,
  getService,
  listCategories,
  listServiceGroups,
  listServiceMedia,
  listServiceVariants,
  removeServiceMedia,
  setServiceActive,
  setServiceFeatured,
  setServiceVariantActive,
  updateService,
  updateServiceVariant,
} from "@/lib/catalog-api";
import type { ServiceVariantAdminResponse } from "@/lib/catalog-types";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { MealRatesCard } from "./MealRatesCard";

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
  cancellationPolicy: z.string().max(2000).optional().or(z.literal("")),
  reschedulePolicy: z.string().max(2000).optional().or(z.literal("")),
  sortOrder: z.number().int().min(0),
  serviceGroupId: z.string().optional().or(z.literal("")),
  seoTitle: z.string().max(200).optional().or(z.literal("")),
  seoMetaDescription: z.string().max(500).optional().or(z.literal("")),
  pricingType: z.enum(["Fixed", "Variable", "MealBased"]),
  isTaxApplicable: z.boolean(),
  isAddOnAllowed: z.boolean(),
  isQuantityAllowed: z.boolean(),
  isDurationBased: z.boolean(),
  isInspectionBased: z.boolean(),
  isSlotRequired: z.boolean(),
  isAddressRequired: z.boolean(),
  isCustomerNoteAllowed: z.boolean(),
});
type ServiceFormValues = z.infer<typeof serviceSchema>;

const mediaSchema = z.object({ url: z.string().min(1, "Image URL is required").max(1000) });
type MediaFormValues = z.infer<typeof mediaSchema>;

const variantSchema = z.object({
  name: z.string().min(1, "Variant name is required").max(200),
  price: z.number().positive("Price must be greater than 0"),
  durationMinutes: z.number().int().positive("Duration must be greater than 0"),
  inclusionsOverride: z.string().max(4000).optional().or(z.literal("")),
  sortOrder: z.number().int().min(0),
});
type VariantFormValues = z.infer<typeof variantSchema>;
const emptyVariantForm: VariantFormValues = { name: "", price: 0, durationMinutes: 60, inclusionsOverride: "", sortOrder: 0 };

/** Edit screen for one service/package (SRS 12.6, task 106): every field, option flags, gallery media, activation and featuring. */
export default function EditServicePage() {
  const { serviceId } = useParams<{ serviceId: string }>();
  const queryClient = useQueryClient();
  const claims = useAdminClaims();
  const [confirmDeactivate, setConfirmDeactivate] = useState(false);
  const [pendingMediaRemoval, setPendingMediaRemoval] = useState<{ id: string; url: string } | null>(null);
  const [editingVariantId, setEditingVariantId] = useState<string | null>(null);

  const canWrite = canWriteModule(claims, "catalog");

  const serviceQuery = useQuery({ queryKey: ["services", serviceId], queryFn: () => getService(serviceId) });
  const categoriesQuery = useQuery({ queryKey: ["categories"], queryFn: listCategories });
  const mediaQuery = useQuery({ queryKey: ["service-media", serviceId], queryFn: () => listServiceMedia(serviceId) });
  const variantsQuery = useQuery({ queryKey: ["service-variants", serviceId], queryFn: () => listServiceVariants(serviceId) });

  const categoryOptions = (categoriesQuery.data ?? []).map((c) => ({ value: c.id, label: c.name }));

  const form = useForm<ServiceFormValues>({
    resolver: zodResolver(serviceSchema),
    defaultValues: {
      isTaxApplicable: false,
      isAddOnAllowed: false,
      isQuantityAllowed: false,
      isInspectionBased: false,
      isSlotRequired: false,
      isAddressRequired: false,
      isCustomerNoteAllowed: false,
      isDurationBased: false,
    },
    values: serviceQuery.data
      ? {
          categoryId: serviceQuery.data.categoryId,
          name: serviceQuery.data.name,
          slug: serviceQuery.data.slug,
          shortDescription: serviceQuery.data.shortDescription ?? "",
          description: serviceQuery.data.description,
          price: serviceQuery.data.price,
          coverImageUrl: serviceQuery.data.coverImageUrl ?? "",
          durationMinutes: serviceQuery.data.durationMinutes,
          inclusions: serviceQuery.data.inclusions,
          exclusions: serviceQuery.data.exclusions,
          cancellationPolicy: serviceQuery.data.cancellationPolicy ?? "",
          reschedulePolicy: serviceQuery.data.reschedulePolicy ?? "",
          sortOrder: serviceQuery.data.sortOrder,
          serviceGroupId: serviceQuery.data.serviceGroupId ?? "",
          seoTitle: serviceQuery.data.seoTitle ?? "",
          seoMetaDescription: serviceQuery.data.seoMetaDescription ?? "",
          pricingType: serviceQuery.data.pricingType,
          isTaxApplicable: serviceQuery.data.isTaxApplicable,
          isAddOnAllowed: serviceQuery.data.isAddOnAllowed,
          isQuantityAllowed: serviceQuery.data.isQuantityAllowed,
          isInspectionBased: serviceQuery.data.isInspectionBased,
          isSlotRequired: serviceQuery.data.isSlotRequired,
          isAddressRequired: serviceQuery.data.isAddressRequired,
          isCustomerNoteAllowed: serviceQuery.data.isCustomerNoteAllowed,
          isDurationBased: serviceQuery.data.isDurationBased,
        }
      : undefined,
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

  const updateMutation = useMutation({
    mutationFn: (values: ServiceFormValues) =>
      updateService(serviceId, {
        categoryId: values.categoryId,
        name: values.name,
        slug: values.slug,
        description: values.description,
        shortDescription: values.shortDescription || null,
        price: values.price,
        coverImageUrl: values.coverImageUrl || null,
        inclusions: values.inclusions,
        exclusions: values.exclusions,
        cancellationPolicy: values.cancellationPolicy || null,
        reschedulePolicy: values.reschedulePolicy || null,
        durationMinutes: values.durationMinutes,
        sortOrder: values.sortOrder,
        serviceGroupId: values.serviceGroupId || null,
        seoTitle: values.seoTitle || null,
        seoMetaDescription: values.seoMetaDescription || null,
        pricingType: values.pricingType,
        isTaxApplicable: values.isTaxApplicable,
        isAddOnAllowed: values.isAddOnAllowed,
        isQuantityAllowed: values.isQuantityAllowed,
        isInspectionBased: values.isInspectionBased,
        isSlotRequired: values.isSlotRequired,
        isAddressRequired: values.isAddressRequired,
        isCustomerNoteAllowed: values.isCustomerNoteAllowed,
        isDurationBased: values.isDurationBased,
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["services"] }),
  });

  const activeMutation = useMutation({
    mutationFn: (isActive: boolean) => setServiceActive(serviceId, isActive),
    onSuccess: () => {
      setConfirmDeactivate(false);
      queryClient.invalidateQueries({ queryKey: ["services"] });
    },
  });

  const featuredMutation = useMutation({
    mutationFn: (isFeatured: boolean) => setServiceFeatured(serviceId, isFeatured),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["services"] }),
  });

  const mediaForm = useForm<MediaFormValues>({ resolver: zodResolver(mediaSchema), defaultValues: { url: "" } });

  const addMediaMutation = useMutation({
    mutationFn: (values: MediaFormValues) => addServiceMedia(serviceId, values),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["service-media", serviceId] });
      mediaForm.reset();
    },
  });

  const removeMediaMutation = useMutation({
    mutationFn: (mediaId: string) => removeServiceMedia(serviceId, mediaId),
    onSuccess: () => {
      setPendingMediaRemoval(null);
      queryClient.invalidateQueries({ queryKey: ["service-media", serviceId] });
    },
  });

  const variantForm = useForm<VariantFormValues>({ resolver: zodResolver(variantSchema), defaultValues: emptyVariantForm });

  const createVariantMutation = useMutation({
    mutationFn: (values: VariantFormValues) =>
      createServiceVariant(serviceId, { ...values, inclusionsOverride: values.inclusionsOverride || null }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["service-variants", serviceId] });
      variantForm.reset(emptyVariantForm);
    },
  });

  const updateVariantMutation = useMutation({
    mutationFn: ({ id, values }: { id: string; values: VariantFormValues }) =>
      updateServiceVariant(serviceId, id, { ...values, inclusionsOverride: values.inclusionsOverride || null }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["service-variants", serviceId] });
      setEditingVariantId(null);
      variantForm.reset(emptyVariantForm);
    },
  });

  const toggleVariantActiveMutation = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) => setServiceVariantActive(serviceId, id, isActive),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["service-variants", serviceId] }),
  });

  const deleteVariantMutation = useMutation({
    mutationFn: (id: string) => deleteServiceVariant(serviceId, id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["service-variants", serviceId] }),
  });

  const startEditingVariant = (variant: ServiceVariantAdminResponse) => {
    setEditingVariantId(variant.id);
    variantForm.reset({
      name: variant.name,
      price: variant.price,
      durationMinutes: variant.durationMinutes,
      inclusionsOverride: variant.inclusionsOverride ?? "",
      sortOrder: variant.sortOrder,
    });
  };

  const cancelEditingVariant = () => {
    setEditingVariantId(null);
    variantForm.reset(emptyVariantForm);
  };

  const onSubmit = form.handleSubmit((values) => updateMutation.mutate(values));
  const onAddMedia = mediaForm.handleSubmit((values) => addMediaMutation.mutate(values));
  const onSubmitVariant = variantForm.handleSubmit((values) =>
    editingVariantId
      ? updateVariantMutation.mutate({ id: editingVariantId, values })
      : createVariantMutation.mutate(values),
  );

  const breadcrumbs = [
    { label: "Catalog", href: "/catalog" },
    { label: "Services", href: "/catalog/services" },
    { label: serviceQuery.data?.name ?? "Service" },
  ];

  if (serviceQuery.isPending) {
    return <DetailSkeleton cards={3} className="flex w-full max-w-5xl flex-col gap-6" />;
  }

  if (serviceQuery.error || !serviceQuery.data) {
    return (
      <DetailError
        title="Service"
        breadcrumbs={breadcrumbs}
        error={serviceQuery.error}
        message={serviceQuery.error ? undefined : "This service no longer exists."}
        onRetry={() => serviceQuery.refetch()}
        className="w-full max-w-5xl"
      />
    );
  }

  const service = serviceQuery.data;

  return (
    <div className="flex w-full max-w-5xl flex-col gap-6">
      <PageHeading
        title={service.name}
        subtitle="Service details — SRS 12.6.2/12.6.3, full field set and option flags."
        breadcrumbs={<Breadcrumbs items={breadcrumbs} />}
        actions={
          <div className="flex items-center gap-2">
            <StatusBadge isActive={service.isActive} />
            {service.isFeatured ? <Badge tone="accent">Featured</Badge> : null}
          </div>
        }
      />

      {canWrite ? (
        <Card
          title="Visibility"
          description="Applied instantly and recorded to the audit trail — unlike the fields below, which save together."
        >
          <FormActions align="start">
            <Button
              type="button"
              variant="secondary"
              loading={featuredMutation.isPending}
              onClick={() => featuredMutation.mutate(!service.isFeatured)}
            >
              {service.isFeatured ? "Remove from featured" : "Mark as featured"}
            </Button>
            {service.isActive ? (
              <Button type="button" variant="danger" onClick={() => setConfirmDeactivate(true)}>
                Deactivate
              </Button>
            ) : (
              <Button
                type="button"
                variant="subtle"
                loading={activeMutation.isPending}
                onClick={() => activeMutation.mutate(true)}
              >
                Activate
              </Button>
            )}
          </FormActions>
          {featuredMutation.isError ? (
            <div className="mt-4">
              <Alert>{describeError(featuredMutation.error)}</Alert>
            </div>
          ) : null}
        </Card>
      ) : null}

      <Card title="Details" description={canWrite ? undefined : "Read-only — you do not hold catalog write access."}>
        <form onSubmit={onSubmit} className="flex max-w-2xl flex-col gap-4" noValidate>
          {updateMutation.isError ? <Alert>{describeError(updateMutation.error)}</Alert> : null}
          {updateMutation.isSuccess ? <Alert tone="success">Service saved.</Alert> : null}

          <FormGrid>
            <Select
              label="Category"
              required
              options={categoryOptions}
              error={form.formState.errors.categoryId?.message}
              {...form.register("categoryId")}
              disabled={!canWrite}
            />
            <Field label="Name" required error={form.formState.errors.name?.message} {...form.register("name")} disabled={!canWrite} />
          </FormGrid>

          <FormGrid columns={3}>
            <Field
              label="Slug"
              required
              hint="Lowercase letters, numbers and hyphens."
              error={form.formState.errors.slug?.message}
              {...form.register("slug")}
              disabled={!canWrite}
            />
            <Field
              label="Price"
              type="number"
              step="0.01"
              required
              leading="₹"
              error={form.formState.errors.price?.message}
              {...form.register("price", { valueAsNumber: true })}
              disabled={!canWrite}
            />
            <Field
              label="Duration (minutes)"
              type="number"
              required
              error={form.formState.errors.durationMinutes?.message}
              {...form.register("durationMinutes", { valueAsNumber: true })}
              disabled={!canWrite}
            />
          </FormGrid>

          <Field
            label="Short description"
            error={form.formState.errors.shortDescription?.message}
            {...form.register("shortDescription")}
            disabled={!canWrite}
          />
          <Field
            label="Cover image URL"
            hint="Shown on customer-facing listing cards. Leave blank to use a graphic placeholder."
            error={form.formState.errors.coverImageUrl?.message}
            {...form.register("coverImageUrl")}
            disabled={!canWrite}
          />
          <Textarea
            label="Description"
            error={form.formState.errors.description?.message}
            {...form.register("description")}
            disabled={!canWrite}
          />
          <FormGrid>
            <Textarea
              label="Inclusions"
              error={form.formState.errors.inclusions?.message}
              {...form.register("inclusions")}
              disabled={!canWrite}
            />
            <Textarea
              label="Exclusions"
              error={form.formState.errors.exclusions?.message}
              {...form.register("exclusions")}
              disabled={!canWrite}
            />
            <Textarea
              label="Cancellation policy"
              error={form.formState.errors.cancellationPolicy?.message}
              {...form.register("cancellationPolicy")}
              disabled={!canWrite}
            />
            <Textarea
              label="Reschedule policy"
              error={form.formState.errors.reschedulePolicy?.message}
              {...form.register("reschedulePolicy")}
              disabled={!canWrite}
            />
          </FormGrid>

          <FormGrid>
            <Field
              label="Sort order"
              type="number"
              error={form.formState.errors.sortOrder?.message}
              {...form.register("sortOrder", { valueAsNumber: true })}
              disabled={!canWrite}
            />
            <Select
              label="Pricing type"
              options={[
                { value: "Fixed", label: "Fixed package price" },
                { value: "Variable", label: "Variable (base + add-ons)" },
                { value: "MealBased", label: "Meal-based (cook: meals and people)" },
              ]}
              {...form.register("pricingType")}
              disabled={!canWrite}
            />
          </FormGrid>

          <Select
            // The group list loads async (gated on the selected category), so it
            // can still be just "No group" when RHF's reset() applies the saved
            // serviceGroupId to this uncontrolled <select> — the browser silently
            // falls back to the first option since the real one doesn't exist yet,
            // and RHF never re-syncs the DOM value once the missing option shows
            // up. Remounting when the option count changes forces the register()
            // ref callback to re-run and re-apply the current form value against
            // the now-complete option list.
            key={serviceGroupOptions.length}
            label="Service group"
            hint="Optional section header shown on the customer-facing listing (e.g. &ldquo;Repair &amp; gas refill&rdquo;). Leave as No group to show this service directly under the category."
            options={serviceGroupOptions}
            {...form.register("serviceGroupId")}
            disabled={!canWrite}
          />

          <FormGrid>
            <Field label="SEO title" error={form.formState.errors.seoTitle?.message} {...form.register("seoTitle")} disabled={!canWrite} />
            <Field
              label="SEO meta description"
              error={form.formState.errors.seoMetaDescription?.message}
              {...form.register("seoMetaDescription")}
              disabled={!canWrite}
            />
          </FormGrid>

          <fieldset className="grid grid-cols-1 gap-6 sm:grid-cols-2">
            <legend className="mb-2 text-sm font-medium text-fg">Booking options</legend>
            <CheckboxField
              label="Tax applicable"
              checked={form.watch("isTaxApplicable")}
              onChange={(v) => form.setValue("isTaxApplicable", v)}
              disabled={!canWrite}
            />
            <CheckboxField
              label="Add-ons allowed"
              checked={form.watch("isAddOnAllowed")}
              onChange={(v) => form.setValue("isAddOnAllowed", v)}
              disabled={!canWrite}
            />
            <CheckboxField
              label="Quantity allowed"
              checked={form.watch("isQuantityAllowed")}
              onChange={(v) => form.setValue("isQuantityAllowed", v)}
              disabled={!canWrite}
            />
            <CheckboxField
              label="Duration-based (time is what's booked, e.g. hourly)"
              checked={form.watch("isDurationBased")}
              onChange={(v) => form.setValue("isDurationBased", v)}
              disabled={!canWrite}
            />
            <CheckboxField
              label="Inspection required before scheduling"
              checked={form.watch("isInspectionBased")}
              onChange={(v) => form.setValue("isInspectionBased", v)}
              disabled={!canWrite}
            />
            <CheckboxField
              label="Slot required"
              checked={form.watch("isSlotRequired")}
              onChange={(v) => form.setValue("isSlotRequired", v)}
              disabled={!canWrite}
            />
            <CheckboxField
              label="Address required"
              checked={form.watch("isAddressRequired")}
              onChange={(v) => form.setValue("isAddressRequired", v)}
              disabled={!canWrite}
            />
            <CheckboxField
              label="Customer note allowed"
              checked={form.watch("isCustomerNoteAllowed")}
              onChange={(v) => form.setValue("isCustomerNoteAllowed", v)}
              disabled={!canWrite}
            />
          </fieldset>

          {canWrite ? (
            <FormActions>
              <Button type="submit" loading={form.formState.isSubmitting || updateMutation.isPending}>
                Save changes
              </Button>
            </FormActions>
          ) : null}
        </form>
      </Card>

      {serviceQuery.data.pricingType === "MealBased" ? (
        <MealRatesCard key={serviceQuery.data.id} service={serviceQuery.data} canWrite={canWrite} />
      ) : null}

      <Card
        title="Variants"
        description="Priced/timed options a customer can pick between (Phase 3 catalog redesign). Leave empty to keep booking this service at its flat price above."
      >
        {variantsQuery.isPending ? (
          <SkeletonText lines={3} />
        ) : variantsQuery.error ? (
          <SectionError error={variantsQuery.error} onRetry={() => variantsQuery.refetch()} />
        ) : !variantsQuery.data || variantsQuery.data.length === 0 ? (
          <EmptyState
            title="No variants yet"
            description={
              canWrite
                ? "This service books at its flat price above until a variant is added."
                : "An admin with catalog write access can add one."
            }
          />
        ) : (
          <ul className="flex flex-col gap-2">
            {variantsQuery.data.map((variant) => (
              <li
                key={variant.id}
                className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line px-3 py-2 text-sm"
              >
                <div className="min-w-0">
                  <span className="font-medium text-fg">{variant.name}</span>
                  <span className="ml-2 text-fg-muted">
                    {formatCurrency(variant.price)} · {variant.durationMinutes} min
                  </span>
                  {!variant.isActive ? (
                    <Badge tone="neutral" className="ml-2">
                      Inactive
                    </Badge>
                  ) : null}
                </div>
                {canWrite ? (
                  <div className="flex shrink-0 gap-2">
                    <Button type="button" size="sm" variant="secondary" onClick={() => startEditingVariant(variant)}>
                      Edit
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      variant="subtle"
                      loading={toggleVariantActiveMutation.isPending && toggleVariantActiveMutation.variables?.id === variant.id}
                      onClick={() =>
                        toggleVariantActiveMutation.mutate({ id: variant.id, isActive: !variant.isActive })
                      }
                    >
                      {variant.isActive ? "Deactivate" : "Activate"}
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      variant="secondary"
                      loading={deleteVariantMutation.isPending && deleteVariantMutation.variables === variant.id}
                      onClick={() => deleteVariantMutation.mutate(variant.id)}
                    >
                      Delete
                    </Button>
                  </div>
                ) : null}
              </li>
            ))}
          </ul>
        )}

        {canWrite ? (
          <form onSubmit={onSubmitVariant} className="mt-5 flex flex-col gap-4 border-t border-line pt-5" noValidate>
            {(editingVariantId ? updateVariantMutation.isError : createVariantMutation.isError) ? (
              <Alert>{describeError(editingVariantId ? updateVariantMutation.error : createVariantMutation.error)}</Alert>
            ) : null}
            <p className="text-sm font-medium text-fg">{editingVariantId ? "Edit variant" : "Add variant"}</p>
            {/* Explicit ids: this sub-form's field `name`s ("name", "price",
                "durationMinutes", "sortOrder") are the same as the service-level
                form's above, and Field/controlId falls back to `field-${name}`
                when no id is given - without these, the two forms rendered on
                one page duplicate the same DOM id, which breaks label/input
                association (and, in the wild, autofill and getElementById-based
                tooling) for whichever field the browser doesn't pick first. */}
            <FormGrid columns={3}>
              <Field
                id="variant-name"
                label="Name"
                required
                error={variantForm.formState.errors.name?.message}
                {...variantForm.register("name")}
              />
              <Field
                id="variant-price"
                label="Price"
                type="number"
                step="0.01"
                required
                leading="₹"
                error={variantForm.formState.errors.price?.message}
                {...variantForm.register("price", { valueAsNumber: true })}
              />
              <Field
                id="variant-durationMinutes"
                label="Duration (minutes)"
                type="number"
                required
                error={variantForm.formState.errors.durationMinutes?.message}
                {...variantForm.register("durationMinutes", { valueAsNumber: true })}
              />
            </FormGrid>
            <FormGrid>
              <Field
                label="Inclusions override"
                hint="Leave blank to use the service's own inclusions."
                error={variantForm.formState.errors.inclusionsOverride?.message}
                {...variantForm.register("inclusionsOverride")}
              />
              <Field
                id="variant-sortOrder"
                label="Sort order"
                type="number"
                error={variantForm.formState.errors.sortOrder?.message}
                {...variantForm.register("sortOrder", { valueAsNumber: true })}
              />
            </FormGrid>
            <FormActions>
              {editingVariantId ? (
                <Button type="button" variant="secondary" onClick={cancelEditingVariant}>
                  Cancel
                </Button>
              ) : null}
              <Button
                type="submit"
                loading={
                  variantForm.formState.isSubmitting || createVariantMutation.isPending || updateVariantMutation.isPending
                }
              >
                {editingVariantId ? "Save variant" : "Add variant"}
              </Button>
            </FormActions>
          </form>
        ) : null}
      </Card>

      <Card title="Gallery" description="Images shown on the service detail page (SRS 12.6.2 &quot;Gallery images&quot;).">
        {mediaQuery.isPending ? (
          <SkeletonText lines={3} />
        ) : mediaQuery.error ? (
          <SectionError error={mediaQuery.error} onRetry={() => mediaQuery.refetch()} />
        ) : !mediaQuery.data || mediaQuery.data.length === 0 ? (
          <EmptyState
            title="No gallery images yet"
            description={
              canWrite
                ? "Add an image URL below — the service detail page falls back to the category banner until then."
                : "An admin with catalog write access can add one."
            }
          />
        ) : (
          <ul className="flex flex-col gap-2">
            {mediaQuery.data.map((media) => (
              <li
                key={media.id}
                className="flex items-center justify-between gap-3 rounded-xl border border-line px-3 py-2 text-sm"
              >
                <span className="min-w-0 truncate text-fg">{media.url}</span>
                {canWrite ? (
                  <Button
                    type="button"
                    size="sm"
                    variant="secondary"
                    className="shrink-0"
                    onClick={() => setPendingMediaRemoval({ id: media.id, url: media.url })}
                  >
                    Remove
                  </Button>
                ) : null}
              </li>
            ))}
          </ul>
        )}

        {canWrite ? (
          <form onSubmit={onAddMedia} className="mt-5 flex max-w-2xl flex-wrap items-end gap-3 border-t border-line pt-5" noValidate>
            {addMediaMutation.isError ? (
              <div className="w-full">
                <Alert>{describeError(addMediaMutation.error)}</Alert>
              </div>
            ) : null}
            <div className="min-w-64 flex-1">
              <Field label="Image URL" error={mediaForm.formState.errors.url?.message} {...mediaForm.register("url")} />
            </div>
            <Button type="submit" loading={mediaForm.formState.isSubmitting || addMediaMutation.isPending}>
              Add image
            </Button>
          </form>
        ) : null}
      </Card>

      <ConfirmDialog
        open={confirmDeactivate}
        title="Deactivate this service?"
        description="It stops being bookable immediately. Bookings already placed against it are unaffected."
        confirmLabel="Deactivate"
        cancelLabel="Keep active"
        loading={activeMutation.isPending}
        error={activeMutation.isError ? describeError(activeMutation.error) : null}
        onCancel={() => setConfirmDeactivate(false)}
        onConfirm={() => activeMutation.mutate(false)}
      >
        <p className="text-sm text-fg-muted">
          Deactivating <span className="font-medium text-fg">{service.name}</span>.
        </p>
      </ConfirmDialog>

      <ConfirmDialog
        open={pendingMediaRemoval !== null}
        title="Remove this image?"
        description="It disappears from the service's gallery immediately."
        confirmLabel="Remove image"
        cancelLabel="Keep image"
        loading={removeMediaMutation.isPending}
        error={removeMediaMutation.isError ? describeError(removeMediaMutation.error) : null}
        onCancel={() => setPendingMediaRemoval(null)}
        onConfirm={() => {
          if (pendingMediaRemoval) removeMediaMutation.mutate(pendingMediaRemoval.id);
        }}
      >
        {pendingMediaRemoval ? (
          <p className="break-all text-sm text-fg-muted">{pendingMediaRemoval.url}</p>
        ) : null}
      </ConfirmDialog>
    </div>
  );
}
