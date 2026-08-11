"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Alert, Button, CheckboxField, Field, Modal, PageHeading, Select } from "@/components/ui";
import { FormGrid, SearchableSelect, formatCurrency } from "@/components/data-table";
import { EntityTable } from "@/components/entity-table";
import { describeError } from "@/lib/api";
import { createServiceAddOn, listAddOnGroups, listServiceAddOns, listServices, setServiceAddOnActive } from "@/lib/catalog-api";
import type { ServiceAddOnAdminResponse } from "@/lib/catalog-types";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { CatalogTabs } from "../_components/CatalogTabs";

const addOnSchema = z.object({
  serviceId: z.string().min(1, "Select a service"),
  name: z.string().min(1, "Add-on name is required").max(200),
  description: z.string().max(1000).optional().or(z.literal("")),
  price: z.number().positive("Price must be greater than 0"),
  sortOrder: z.number().int().min(0),
  isQuantityAllowed: z.boolean(),
  isMandatory: z.boolean(),
  groupId: z.string().optional().or(z.literal("")),
});
type AddOnFormValues = z.infer<typeof addOnSchema>;

/**
 * Admin add-on management screen (SRS 12.7, tasks 107-108): create add-ons
 * mapped to a service. Editing (including re-mapping to a different service)
 * happens on its own page (`/catalog/addons/[id]`).
 */
export default function CatalogAddOnsPage() {
  const claims = useAdminClaims();
  const [serviceFilter, setServiceFilter] = useState("");
  const [addOpen, setAddOpen] = useState(false);
  const queryClient = useQueryClient();

  const canWrite = canWriteModule(claims, "catalog");

  const servicesQuery = useQuery({ queryKey: ["services"], queryFn: () => listServices() });
  const addOnsQuery = useQuery({
    queryKey: ["service-addons", serviceFilter],
    queryFn: () => listServiceAddOns(serviceFilter || undefined),
  });

  const serviceOptions = (servicesQuery.data ?? []).map((s) => ({ value: s.id, label: s.name }));

  const form = useForm<AddOnFormValues>({
    resolver: zodResolver(addOnSchema),
    defaultValues: {
      serviceId: "",
      name: "",
      description: "",
      price: 0,
      sortOrder: 0,
      isQuantityAllowed: false,
      isMandatory: false,
      groupId: "",
    },
  });

  const formServiceId = form.watch("serviceId");
  const groupsQuery = useQuery({
    queryKey: ["addon-groups", formServiceId],
    queryFn: () => listAddOnGroups(formServiceId || undefined),
    enabled: Boolean(formServiceId),
  });
  const groupOptions = (groupsQuery.data ?? []).map((g) => ({ value: g.id, label: g.name }));

  const createMutation = useMutation({
    mutationFn: createServiceAddOn,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["service-addons"] });
      form.reset({ ...form.getValues(), name: "", description: "", price: 0, groupId: "" });
      setAddOpen(false);
    },
  });

  const toggleMutation = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) => setServiceAddOnActive(id, isActive),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["service-addons"] }),
  });

  const onSubmit = form.handleSubmit((values) =>
    createMutation.mutate({
      serviceId: values.serviceId,
      name: values.name,
      description: values.description || null,
      price: values.price,
      sortOrder: values.sortOrder,
      isQuantityAllowed: values.isQuantityAllowed,
      isMandatory: values.isMandatory,
      groupId: values.groupId || null,
    }),
  );

  return (
    <div className="mx-auto flex w-full max-w-5xl flex-col gap-6">
      <div>
        <PageHeading title="Catalog" subtitle="Categories, services and add-ons (SRS 12.5-12.7)." />
        <CatalogTabs />
      </div>

      <EntityTable<ServiceAddOnAdminResponse>
        title="Add-ons"
        description="Optional or mandatory extras mapped to a service (SRS 12.7)."
        actions={
          <div className="flex flex-wrap items-end gap-2">
            <div className="w-56">
              <Select
                label="Filter by service"
                value={serviceFilter}
                onChange={(e) => setServiceFilter(e.target.value)}
                options={[{ value: "", label: "All services" }, ...serviceOptions]}
              />
            </div>
            {canWrite ? (
              <Button type="button" onClick={() => setAddOpen(true)}>
                Add add-on
              </Button>
            ) : null}
          </div>
        }
        items={addOnsQuery.data}
        isLoading={addOnsQuery.isPending}
        isFetching={addOnsQuery.isFetching}
        error={addOnsQuery.error}
        onRetry={() => addOnsQuery.refetch()}
        emptyMessage={serviceFilter ? "No add-ons for this service" : "No add-ons yet"}
        emptyAction={
          serviceFilter ? (
            <Button variant="secondary" onClick={() => setServiceFilter("")}>
              Show all services
            </Button>
          ) : undefined
        }
        canWrite={canWrite}
        entityLabel="add-on"
        labelOf={(addOn) => addOn.name}
        minWidth="900px"
        togglingId={toggleMutation.isPending ? toggleMutation.variables?.id : undefined}
        toggleError={toggleMutation.error}
        onToggleActive={(addOn) => toggleMutation.mutate({ id: addOn.id, isActive: !addOn.isActive })}
        columns={[
          {
            header: "Name",
            sortValue: (addOn) => addOn.name,
            render: (addOn) => (
              <Link
                href={`/catalog/addons/${addOn.id}`}
                className="font-medium text-fg underline-offset-4 hover:text-brand-600 hover:underline dark:hover:text-brand-400"
              >
                {addOn.name}
              </Link>
            ),
          },
          { header: "Service", sortValue: (addOn) => addOn.serviceName, render: (addOn) => addOn.serviceName },
          {
            header: "Price",
            numeric: true,
            sortValue: (addOn) => addOn.price,
            render: (addOn) => formatCurrency(addOn.price),
          },
          {
            header: "Mandatory",
            sortValue: (addOn) => addOn.isMandatory,
            render: (addOn) => (addOn.isMandatory ? "Yes" : "No"),
          },
          {
            header: "Quantity allowed",
            sortValue: (addOn) => addOn.isQuantityAllowed,
            render: (addOn) => (addOn.isQuantityAllowed ? "Yes" : "No"),
          },
        ]}
      />

      {canWrite ? (
        <Modal
          open={addOpen}
          onClose={() => setAddOpen(false)}
          title="Add add-on"
          description="Creates the add-on immediately; it is offered once activated."
          size="lg"
          footer={
            <>
              <Button type="button" variant="secondary" onClick={() => setAddOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" form="add-addon-form" loading={form.formState.isSubmitting || createMutation.isPending}>
                Add add-on
              </Button>
            </>
          }
        >
          <form id="add-addon-form" onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
            {createMutation.isError ? <Alert>{describeError(createMutation.error)}</Alert> : null}

            <FormGrid>
              <SearchableSelect
                label="Service"
                required
                placeholder="Search services…"
                error={form.formState.errors.serviceId?.message}
                options={serviceOptions}
                value={form.watch("serviceId")}
                onChange={(value) => form.setValue("serviceId", value, { shouldValidate: true })}
              />
              <Field label="Name" required error={form.formState.errors.name?.message} {...form.register("name")} />
            </FormGrid>

            <Field label="Description" error={form.formState.errors.description?.message} {...form.register("description")} />

            <FormGrid>
              <Field
                label="Price"
                type="number"
                step="0.01"
                required
                leading="₹"
                error={form.formState.errors.price?.message}
                {...form.register("price", { valueAsNumber: true })}
              />
              <Field label="Sort order" type="number" error={form.formState.errors.sortOrder?.message} {...form.register("sortOrder", { valueAsNumber: true })} />
            </FormGrid>

            <Select
              label="Group"
              hint={formServiceId ? "Optional — place this add-on under a pick-one/pick-many group (Phase 3)." : "Select a service first."}
              placeholder="Ungrouped"
              disabled={!formServiceId}
              options={groupOptions}
              {...form.register("groupId")}
            />

            <fieldset className="grid grid-cols-1 gap-2 sm:grid-cols-2">
              <legend className="mb-2 text-sm font-medium text-fg">Add-on options</legend>
              <CheckboxField
                label="Quantity allowed"
                checked={form.watch("isQuantityAllowed")}
                onChange={(v) => form.setValue("isQuantityAllowed", v)}
              />
              <CheckboxField
                label="Mandatory"
                description="Included automatically whenever the service is booked."
                checked={form.watch("isMandatory")}
                onChange={(v) => form.setValue("isMandatory", v)}
              />
            </fieldset>
          </form>
        </Modal>
      ) : null}
    </div>
  );
}
