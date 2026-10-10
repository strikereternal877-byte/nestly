"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useParams } from "next/navigation";
import { useEffect, useMemo, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Breadcrumbs, ConfirmDialog, DataTable, FormActions, FormGrid } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { DetailError, DetailSkeleton } from "@/components/screen-states";
import { Alert, Button, Card, Checkbox, Field, PageHeading, Textarea, useToast } from "@/components/ui";
import {
  getAdminPermissionCatalog,
  getAdminRole,
  setAdminRolePermissions,
  updateAdminRole,
} from "@/lib/admin-roles-api";
import { AdminPermissionAction } from "@/lib/admin-roles-types";
import type { AdminPermissionCatalogEntry } from "@/lib/admin-roles-types";
import { describeError } from "@/lib/api";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";

const profileSchema = z.object({
  name: z.string().trim().min(1, "Name is required").max(100),
  description: z.string().trim().max(1000),
});
type ProfileFormValues = z.infer<typeof profileSchema>;

interface ModuleRow {
  module: string;
  readCode: string;
  writeCode: string;
}

function buildModuleRows(catalog: AdminPermissionCatalogEntry[]): ModuleRow[] {
  const byModule = new Map<string, ModuleRow>();
  for (const entry of catalog) {
    const row = byModule.get(entry.module) ?? { module: entry.module, readCode: "", writeCode: "" };
    if (entry.action === AdminPermissionAction.Read) row.readCode = entry.code;
    else row.writeCode = entry.code;
    byModule.set(entry.module, row);
  }
  return Array.from(byModule.values()).sort((a, b) => a.module.localeCompare(b.module));
}

/**
 * The matrix's three columns (task 367). Built per render rather than at
 * module scope because every cell is a live control bound to the current
 * selection - `columns` is not static data here the way it is on a read-only
 * list screen.
 *
 * No column carries `sortValue`: `buildModuleRows` already returns the
 * catalog in the only order that makes sense for a grid you read down, and a
 * sortable Read/Write column would offer to reorder modules by checkbox
 * state, which is not a question anyone asks of a permission matrix.
 */
function buildPermissionColumns(
  selectedCodes: Set<string>,
  canWrite: boolean,
  toggleRead: (row: ModuleRow, checked: boolean) => void,
  toggleWrite: (row: ModuleRow, checked: boolean) => void,
): DataTableColumn<ModuleRow>[] {
  return [
    {
      key: "module",
      header: "Module",
      cell: (row) => <span className="font-medium capitalize text-fg">{row.module}</span>,
    },
    {
      key: "read",
      header: "Read",
      cell: (row) => (
        <Checkbox
          // No visible label text on purpose. Both layouts already name this
          // control beside it - the "Read" column header in the `<table>`, the
          // "READ" <dt> in the card DataTable collapses to below `lg` - so a
          // word on the checkbox itself printed "Read" twice on every one of
          // the 44 rows once this screen moved onto the shared component. A
          // bare checkbox under a labelled header is the conventional
          // permission-matrix shape anyway.
          label=""
          // The accessible name has to carry what the visible layout carries
          // positionally, and it cannot be just "Read": that would leave 22
          // identically-named controls in a screen reader's form-controls
          // list, disambiguated only by a row header the card layout does not
          // have (there the module is a sibling <dd>, not a header).
          aria-label={`Read ${row.module}`}
          checked={selectedCodes.has(row.readCode)}
          disabled={!canWrite || selectedCodes.has(row.writeCode)}
          onChange={(event) => toggleRead(row, event.target.checked)}
        />
      ),
    },
    {
      key: "write",
      header: "Write",
      cell: (row) => (
        <Checkbox
          label=""
          aria-label={`Write ${row.module}`}
          checked={selectedCodes.has(row.writeCode)}
          disabled={!canWrite}
          onChange={(event) => toggleWrite(row, event.target.checked)}
        />
      ),
    },
  ];
}

/**
 * Role detail (SRS 12.2.2, 12.2.3, task 313): rename/describe the role, and
 * the permission-matrix editor - a checkbox grid of every module x
 * Read/Write, replacing the role's entire grant on save
 * (`setAdminRolePermissions` is a full-replace, not a delta - see the
 * backend contract's doc comment).
 *
 * The server enforces a self-escalation guard on every save: the response
 * is a 403 ("AdminRole.SelfEscalationBlocked") if this would grant the role
 * a permission the signed-in admin does not already hold themselves -
 * `describeError` surfaces that as plain text in the save error banner, the
 * same as any other rejected mutation on this page.
 *
 * Every save goes through `ConfirmDialog`: unlike a single admin account's
 * status, a role's grant can apply to many signed-in admins at once, and a
 * change - addition or removal - takes effect for all of them immediately.
 */
export default function AdminRoleDetailPage() {
  const { roleId } = useParams<{ roleId: string }>();
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "settings");
  const queryClient = useQueryClient();
  const pushToast = useToast();

  const [selectedCodes, setSelectedCodes] = useState<Set<string>>(new Set());
  const [confirmSavePermissions, setConfirmSavePermissions] = useState(false);

  const detailQuery = useQuery({ queryKey: ["admin-role-detail", roleId], queryFn: () => getAdminRole(roleId) });
  const catalogQuery = useQuery({ queryKey: ["admin-permission-catalog"], queryFn: getAdminPermissionCatalog });

  const moduleRows = useMemo(() => buildModuleRows(catalogQuery.data ?? []), [catalogQuery.data]);

  const form = useForm<ProfileFormValues>({
    resolver: zodResolver(profileSchema),
    defaultValues: { name: "", description: "" },
  });

  const loadedId = detailQuery.data?.id;
  useEffect(() => {
    if (detailQuery.data) {
      form.reset({ name: detailQuery.data.name, description: detailQuery.data.description });
      // Reacting to the role query arriving, not a render-time prop change.
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setSelectedCodes(new Set(detailQuery.data.permissionCodes));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [roleId, loadedId]);

  const refreshRole = () => {
    queryClient.invalidateQueries({ queryKey: ["admin-role-detail", roleId] });
    queryClient.invalidateQueries({ queryKey: ["admin-roles"] });
  };

  const updateMutation = useMutation({
    mutationFn: (values: ProfileFormValues) =>
      updateAdminRole(roleId, { name: values.name.trim(), description: values.description.trim() }),
    onSuccess: () => {
      refreshRole();
      pushToast("success", "Role saved.");
    },
  });

  const permissionsMutation = useMutation({
    mutationFn: () => setAdminRolePermissions(roleId, { permissionCodes: Array.from(selectedCodes) }),
    onSuccess: () => {
      setConfirmSavePermissions(false);
      refreshRole();
      pushToast("success", "Permissions updated.");
    },
  });

  const toggleRead = (row: ModuleRow, checked: boolean) => {
    setSelectedCodes((current) => {
      const next = new Set(current);
      if (checked) {
        next.add(row.readCode);
      } else if (!next.has(row.writeCode)) {
        // Write implies Read (AdminPermissionCatalog's own grant-building
        // rule) - Read cannot be unchecked while Write is still granted.
        next.delete(row.readCode);
      }
      return next;
    });
  };

  const toggleWrite = (row: ModuleRow, checked: boolean) => {
    setSelectedCodes((current) => {
      const next = new Set(current);
      if (checked) {
        next.add(row.writeCode);
        next.add(row.readCode);
      } else {
        next.delete(row.writeCode);
      }
      return next;
    });
  };

  const permissionColumns = buildPermissionColumns(selectedCodes, canWrite, toggleRead, toggleWrite);

  const breadcrumbs = [
    { label: "Admin users", href: "/admin-users" },
    { label: "Roles", href: "/admin-users/roles" },
    { label: detailQuery.data?.name ?? "Role" },
  ];

  if (detailQuery.isPending) {
    return <DetailSkeleton cards={2} className="flex w-full max-w-5xl flex-col gap-6" />;
  }

  if (detailQuery.error || !detailQuery.data) {
    return (
      <DetailError
        title="Role"
        breadcrumbs={breadcrumbs}
        error={detailQuery.error}
        message={detailQuery.error ? undefined : "This role no longer exists."}
        onRetry={() => detailQuery.refetch()}
        className="w-full max-w-5xl"
      />
    );
  }

  const role = detailQuery.data;
  const originalCodes = new Set(role.permissionCodes);
  const hasChanges =
    selectedCodes.size !== originalCodes.size || Array.from(selectedCodes).some((code) => !originalCodes.has(code));
  const removedCodes = role.permissionCodes.filter((code) => !selectedCodes.has(code));

  return (
    <div className="flex w-full max-w-5xl flex-col gap-6">
      <PageHeading
        title={role.name}
        subtitle="Role details and the exact permission-matrix row it grants (SRS 12.2.2, 12.2.3)."
        breadcrumbs={<Breadcrumbs items={breadcrumbs} />}
      />

      <Card title="Role" description={canWrite ? undefined : "Read-only — you do not hold settings write access."}>
        <form
          onSubmit={form.handleSubmit((values) => updateMutation.mutate(values))}
          className="flex max-w-2xl flex-col gap-5"
          noValidate
        >
          {updateMutation.isError ? <Alert>{describeError(updateMutation.error)}</Alert> : null}

          <FormGrid columns={1}>
            <Field
              label="Name"
              required
              disabled={!canWrite}
              error={form.formState.errors.name?.message}
              {...form.register("name")}
            />
            <Textarea
              label="Description"
              disabled={!canWrite}
              error={form.formState.errors.description?.message}
              {...form.register("description")}
            />
          </FormGrid>

          {canWrite ? (
            <FormActions>
              <Button type="submit" loading={updateMutation.isPending}>
                Save role
              </Button>
            </FormActions>
          ) : null}
        </form>
      </Card>

      {/*
       * Task 367: the shared DataTable, not a hand-rolled `<table>`. This was
       * the last table in admin-web bypassing it, and the one that therefore
       * did not inherit task 348's card collapse below `lg` - it did not
       * break at the 768px tablet floor (three narrow columns), but being the
       * single exception meant any future table fix would have to be made
       * twice. The card/list layout, loading skeleton, error and empty states
       * below all come from the component; nothing here re-implements them.
       */}
      <DataTable<ModuleRow>
        title="Permission matrix"
        description="Which modules this role can view (Read) or change (Write). Write always implies Read."
        columns={permissionColumns}
        rows={moduleRows}
        rowKey={(row) => row.module}
        isLoading={catalogQuery.isPending}
        error={catalogQuery.error}
        errorMessage={
          catalogQuery.error
            ? `The permission catalog could not be loaded, so the matrix cannot be edited right now. ${describeError(catalogQuery.error)}`
            : null
        }
        onRetry={() => catalogQuery.refetch()}
        caption="Every admin module, with this role's Read and Write grant on each"
        minWidth="480px"
        skeletonRows={8}
        // Fixed-length and short: the catalog is every module the platform
        // has, so there is nothing for a density preference to help with.
        hideDensityToggle
        emptyTitle="No permission modules"
        emptyDescription="The permission catalog came back empty, so there is nothing to grant here."
        footer={
          canWrite ? (
            <div className="flex flex-col gap-4">
              {permissionsMutation.isError && !confirmSavePermissions ? (
                <Alert>{describeError(permissionsMutation.error)}</Alert>
              ) : null}
              <FormActions>
                <Button
                  type="button"
                  disabled={!hasChanges}
                  onClick={() => setConfirmSavePermissions(true)}
                >
                  Save permissions
                </Button>
              </FormActions>
            </div>
          ) : null
        }
      />

      <ConfirmDialog
        open={confirmSavePermissions}
        title="Update this role's permissions?"
        description="Every admin currently assigned this role is affected immediately, including any signed-in session."
        confirmLabel="Save permissions"
        cancelLabel="Cancel"
        loading={permissionsMutation.isPending}
        error={permissionsMutation.error ? describeError(permissionsMutation.error) : null}
        onCancel={() => setConfirmSavePermissions(false)}
        onConfirm={() => permissionsMutation.mutate()}
      >
        <p className="text-sm text-fg-muted">
          {removedCodes.length > 0
            ? `This removes ${removedCodes.length} permission(s) currently granted.`
            : "This only adds permissions - nothing currently granted is removed."}
        </p>
      </ConfirmDialog>
    </div>
  );
}
