"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { DataTable, formatCurrency } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { Alert, Badge, Button, Field, Modal, PageHeading, Select, Textarea, useToast } from "@/components/ui";
import { describeError } from "@/lib/api";
import { listServices } from "@/lib/catalog-api";
import { canWriteModule } from "@/lib/permissions";
import { listCities } from "@/lib/serviceability-api";
import { useAdminClaims } from "@/lib/use-admin-claims";
import {
  MonthlyServicePlanBasis,
  createMonthlyPlan,
  listMonthlyPlans,
  setMonthlyPlanActive,
  updateMonthlyPlan,
} from "../_lib/monthly-service-api";
import type { MonthlyServicePlanAdmin, MonthlyServicePlanRequest } from "../_lib/monthly-service-api";
import { MONTHLY_KEYS, MonthlyServiceTabs } from "../_components/shared";

/**
 * Monthly Service plan catalog (docs/MONTHLY-SERVICE.md): per city and
 * service, hourly or task-based, priced per visit with a platform
 * commission. Edits never change running engagements - their terms were
 * snapshotted when requested. Gated on subscription.* like the AMC catalog.
 */
export default function MonthlyServicePlansPage() {
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "subscription");
  const toast = useToast();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<MonthlyServicePlanAdmin | "new" | null>(null);

  const query = useQuery({ queryKey: MONTHLY_KEYS.plans, queryFn: listMonthlyPlans });

  const toggle = useMutation({
    mutationFn: (plan: MonthlyServicePlanAdmin) => setMonthlyPlanActive(plan.id, !plan.isActive),
    onSuccess: (_, plan) => {
      toast("success", `${plan.name} ${plan.isActive ? "deactivated" : "activated"}.`);
      void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.plans });
    },
    onError: (error) => toast("error", describeError(error)),
  });

  const columns: DataTableColumn<MonthlyServicePlanAdmin>[] = [
    {
      key: "name",
      header: "Plan",
      cell: (p) => (
        <div className="min-w-0">
          <p className="truncate font-medium text-fg">{p.name}</p>
          <p className="text-xs text-fg-muted">
            {p.serviceName} · {p.cityName}
          </p>
        </div>
      ),
      sortValue: (p) => p.name,
    },
    {
      key: "visit",
      header: "Visit",
      cell: (p) =>
        p.basis === MonthlyServicePlanBasis.Hourly ? `${p.hoursPerVisit} h` : `${p.includedTasks.length} task(s)`,
    },
    { key: "rate", header: "Per visit", numeric: true, cell: (p) => formatCurrency(p.ratePerVisit), sortValue: (p) => p.ratePerVisit },
    { key: "commission", header: "Commission", numeric: true, cell: (p) => `${p.commissionPercent}%`, sortValue: (p) => p.commissionPercent },
    { key: "contracts", header: "Engagements", numeric: true, cell: (p) => p.activeContractCount, sortValue: (p) => p.activeContractCount },
    {
      key: "active",
      header: "Status",
      cell: (p) => <Badge tone={p.isActive ? "success" : "neutral"}>{p.isActive ? "Active" : "Inactive"}</Badge>,
      sortValue: (p) => p.isActive,
    },
  ];

  const newButton = canWrite ? (
    <Button size="sm" onClick={() => setEditing("new")}>
      New plan
    </Button>
  ) : undefined;

  return (
    <div className="flex w-full flex-col gap-6">
      <PageHeading title="Monthly Service" subtitle="Plans customers can request, per city. Priced per visit; billed monthly on attendance." actions={newButton} />
      <MonthlyServiceTabs />
      <DataTable
        title="Plans"
        columns={columns}
        rows={query.data}
        rowKey={(p) => p.id}
        isLoading={query.isPending}
        isFetching={query.isFetching}
        error={query.error}
        onRetry={() => void query.refetch()}
        minWidth="820px"
        caption="Monthly service plans"
        emptyTitle="No plans yet"
        emptyDescription="Create a plan (e.g. House help, 2 hours, Jaipur) so customers can request it."
        emptyAction={newButton}
        rowActions={
          canWrite
            ? (p) => (
                <div className="flex gap-1">
                  <Button size="sm" variant="ghost" onClick={() => setEditing(p)}>
                    Edit
                  </Button>
                  <Button size="sm" variant="ghost" loading={toggle.isPending && toggle.variables?.id === p.id} onClick={() => toggle.mutate(p)}>
                    {p.isActive ? "Deactivate" : "Activate"}
                  </Button>
                </div>
              )
            : undefined
        }
      />
      <PlanFormModal editing={editing} onClose={() => setEditing(null)} />
    </div>
  );
}

function PlanFormModal({ editing, onClose }: { editing: MonthlyServicePlanAdmin | "new" | null; onClose: () => void }) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const open = editing !== null;
  const plan = editing === "new" ? null : editing;

  const services = useQuery({ queryKey: ["catalog-services-all"], queryFn: () => listServices(), enabled: open });
  const cities = useQuery({ queryKey: ["geography-cities"], queryFn: () => listCities(), enabled: open });

  const [form, setForm] = useState({
    serviceId: "",
    cityId: "",
    name: "",
    description: "",
    basis: MonthlyServicePlanBasis.Hourly,
    hoursPerVisit: "2",
    tasks: "",
    ratePerVisit: "",
    commissionPercent: "10",
  });
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setError(null);
    setForm(
      plan
        ? {
            serviceId: plan.serviceId,
            cityId: plan.cityId,
            name: plan.name,
            description: plan.description ?? "",
            basis: plan.basis,
            hoursPerVisit: plan.hoursPerVisit ? String(plan.hoursPerVisit) : "",
            tasks: plan.includedTasks.join("\n"),
            ratePerVisit: String(plan.ratePerVisit),
            commissionPercent: String(plan.commissionPercent),
          }
        : { serviceId: "", cityId: "", name: "", description: "", basis: MonthlyServicePlanBasis.Hourly, hoursPerVisit: "2", tasks: "", ratePerVisit: "", commissionPercent: "10" },
    );
  }, [open, plan]);

  const save = useMutation({
    mutationFn: (request: MonthlyServicePlanRequest) => (plan ? updateMonthlyPlan(plan.id, request) : createMonthlyPlan(request)),
    onSuccess: (saved) => {
      toast("success", `${saved.name} ${plan ? "updated" : "created"}.`);
      void queryClient.invalidateQueries({ queryKey: MONTHLY_KEYS.plans });
      onClose();
    },
    onError: (e) => setError(describeError(e)),
  });

  const set = (key: keyof typeof form) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [key]: e.target.value }));

  const submit = () => {
    const tasks = form.tasks.split("\n").map((t) => t.trim()).filter(Boolean);
    const rate = Number(form.ratePerVisit);
    const commission = Number(form.commissionPercent);
    const hours = Number(form.hoursPerVisit);
    if (!form.serviceId || !form.cityId || !form.name.trim()) return setError("Service, city and name are required.");
    if (!(rate > 0)) return setError("Rate per visit must be more than zero.");
    if (!(commission >= 0 && commission <= 50)) return setError("Commission must be between 0 and 50%.");
    if (form.basis === MonthlyServicePlanBasis.Hourly && !(hours > 0 && hours <= 12)) return setError("Hours per visit must be between 0 and 12.");
    if (form.basis === MonthlyServicePlanBasis.TaskBased && tasks.length === 0) return setError("List at least one task, one per line.");
    setError(null);
    save.mutate({
      serviceId: form.serviceId,
      cityId: form.cityId,
      name: form.name.trim(),
      description: form.description.trim() || null,
      basis: form.basis,
      hoursPerVisit: form.basis === MonthlyServicePlanBasis.Hourly ? hours : null,
      includedTasks: tasks,
      ratePerVisit: rate,
      commissionPercent: commission,
    });
  };

  return (
    <Modal
      open={open}
      onClose={() => !save.isPending && onClose()}
      title={plan ? `Edit ${plan.name}` : "New monthly plan"}
      description={plan ? "Running engagements keep the terms they were requested on." : undefined}
      size="lg"
      footer={
        <>
          <Button variant="secondary" disabled={save.isPending} onClick={onClose}>
            Cancel
          </Button>
          <Button loading={save.isPending} onClick={submit}>
            {plan ? "Save changes" : "Create plan"}
          </Button>
        </>
      }
    >
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <Select
          label="Service"
          value={form.serviceId}
          onChange={set("serviceId")}
          options={[
            { value: "", label: services.isPending ? "Loading…" : "Select a service" },
            ...(services.data ?? []).filter((s) => s.isActive || s.id === form.serviceId).map((s) => ({ value: s.id, label: s.name })),
          ]}
        />
        <Select
          label="City"
          value={form.cityId}
          onChange={set("cityId")}
          options={[
            { value: "", label: cities.isPending ? "Loading…" : "Select a city" },
            ...(cities.data ?? []).map((c) => ({ value: c.id, label: c.name })),
          ]}
        />
        <Field label="Plan name" placeholder="House help — 2 hours" maxLength={150} value={form.name} onChange={set("name")} />
        <Select
          label="Visit basis"
          value={String(form.basis)}
          onChange={(e) => setForm((f) => ({ ...f, basis: Number(e.target.value) as MonthlyServicePlanBasis }))}
          options={[
            { value: String(MonthlyServicePlanBasis.Hourly), label: "Hourly (fixed hours per visit)" },
            { value: String(MonthlyServicePlanBasis.TaskBased), label: "Task-based (fixed tasks per visit)" },
          ]}
        />
        {form.basis === MonthlyServicePlanBasis.Hourly ? (
          <Field label="Hours per visit" type="number" min={0.5} max={12} step={0.5} value={form.hoursPerVisit} onChange={set("hoursPerVisit")} />
        ) : (
          <div />
        )}
        <Field label="Rate per visit (₹)" type="number" min={1} step={1} value={form.ratePerVisit} onChange={set("ratePerVisit")} />
        <Field
          label="Platform commission (%)"
          type="number"
          min={0}
          max={50}
          step={0.5}
          hint="Deducted from each paid invoice before the professional's share."
          value={form.commissionPercent}
          onChange={set("commissionPercent")}
        />
        <div className="sm:col-span-2">
          <Textarea
            label={form.basis === MonthlyServicePlanBasis.TaskBased ? "Included tasks (one per line)" : "Included tasks (optional, one per line)"}
            rows={4}
            placeholder={"Sweeping\nMopping\nDishes"}
            value={form.tasks}
            onChange={set("tasks")}
          />
        </div>
        <div className="sm:col-span-2">
          <Textarea label="Description (optional)" maxLength={500} value={form.description} onChange={set("description")} />
        </div>
        {error ? (
          <div className="sm:col-span-2">
            <Alert tone="error">{error}</Alert>
          </div>
        ) : null}
      </div>
    </Modal>
  );
}
