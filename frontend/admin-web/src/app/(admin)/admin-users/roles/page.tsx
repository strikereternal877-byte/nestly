"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Breadcrumbs, DataTable, FormGrid, formatDate } from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { Alert, Button, Field, Modal, PageHeading, Textarea } from "@/components/ui";
import { createAdminRole, listAdminRolesWithPermissions } from "@/lib/admin-roles-api";
import type { AdminRoleDetail } from "@/lib/admin-roles-types";
import { describeError } from "@/lib/api";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { useState } from "react";

const createSchema = z.object({
  name: z.string().trim().min(1, "Name is required").max(100),
  description: z.string().trim().max(1000),
});
type CreateFormValues = z.infer<typeof createSchema>;

/**
 * Role list and creation (SRS 12.2.2, task 313): AdminPermissionCatalog's
 * nine seeded roles used to be the only roles that could ever exist - this
 * page is where a Super Admin defines more. Permissions for a role (create
 * or existing) are edited on its own detail page
 * (`roles/[roleId]/page.tsx`'s matrix editor), not here - same split
 * `admin-users/page.tsx` uses between account creation and role assignment.
 */
export default function AdminRolesPage() {
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "settings");
  const queryClient = useQueryClient();
  const [isCreating, setIsCreating] = useState(false);

  const rolesQuery = useQuery({ queryKey: ["admin-roles"], queryFn: listAdminRolesWithPermissions });

  const createMutation = useMutation({
    mutationFn: (values: CreateFormValues) =>
      createAdminRole({ name: values.name.trim(), description: values.description.trim(), permissionCodes: [] }),
    onSuccess: () => {
      setIsCreating(false);
      queryClient.invalidateQueries({ queryKey: ["admin-roles"] });
    },
  });

  const columns: DataTableColumn<AdminRoleDetail>[] = [
    {
      key: "name",
      header: "Name",
      sortValue: (role) => role.name,
      cell: (role) => (
        <Link
          href={`/admin-users/roles/${role.id}`}
          className="font-medium text-fg underline-offset-4 transition-colors duration-fast ease-out hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {role.name}
        </Link>
      ),
    },
    {
      key: "description",
      header: "Description",
      sortValue: (role) => role.description,
      cell: (role) => role.description || <span className="text-fg-subtle">No description</span>,
    },
    {
      key: "permissions",
      header: "Permissions",
      sortValue: (role) => role.permissionCodes.length,
      cell: (role) => <span className="nums">{role.permissionCodes.length}</span>,
    },
    {
      key: "created",
      header: "Created",
      sortValue: (role) => role.createdAtUtc,
      cell: (role) => <span className="nums whitespace-nowrap">{formatDate(role.createdAtUtc)}</span>,
    },
  ];

  return (
    <div className="w-full max-w-5xl">
      <PageHeading
        title="Roles & permissions"
        subtitle="The permission matrix behind every admin account (SRS 12.2.2, 12.2.3): create roles and edit exactly which modules each one can read or write."
        breadcrumbs={<Breadcrumbs items={[{ label: "Admin users", href: "/admin-users" }, { label: "Roles" }]} />}
        actions={canWrite ? <Button onClick={() => setIsCreating(true)}>Create role</Button> : undefined}
      />

      <DataTable
        title="Roles"
        description="Every role, including the nine seeded defaults - all of them are editable."
        columns={columns}
        rows={rolesQuery.data}
        rowKey={(role) => role.id}
        isLoading={rolesQuery.isPending}
        isFetching={rolesQuery.isFetching}
        error={rolesQuery.error}
        onRetry={() => rolesQuery.refetch()}
        caption="Roles and their permission counts"
        emptyTitle="No roles yet"
        emptyDescription={canWrite ? "Create the first role." : "An admin with settings write access can create one."}
        emptyAction={canWrite ? <Button onClick={() => setIsCreating(true)}>Create role</Button> : undefined}
        skeletonRows={6}
        minWidth="720px"
        rowActions={(role) => (
          <Link
            href={`/admin-users/roles/${role.id}`}
            aria-label={`Manage ${role.name}`}
            className="inline-flex h-8 items-center rounded-lg px-3 text-xs font-medium text-fg-muted transition-colors duration-fast ease-out hover:bg-surface-3 hover:text-fg"
          >
            Manage
          </Link>
        )}
      />

      <CreateAdminRoleModal
        open={isCreating}
        isSubmitting={createMutation.isPending}
        error={createMutation.error}
        onSubmit={(values) => createMutation.mutate(values)}
        onClose={() => {
          if (createMutation.isPending) return;
          createMutation.reset();
          setIsCreating(false);
        }}
      />
    </div>
  );
}

function CreateAdminRoleModal({
  open,
  isSubmitting,
  error,
  onSubmit,
  onClose,
}: {
  open: boolean;
  isSubmitting: boolean;
  error: unknown;
  onSubmit: (values: CreateFormValues) => void;
  onClose: () => void;
}) {
  const form = useForm<CreateFormValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { name: "", description: "" },
  });

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Create role"
      description="Starts with no permissions granted - add them from the role's own page afterwards."
      footer={
        <>
          <Button type="button" variant="secondary" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </Button>
          <Button type="submit" form="create-admin-role-form" loading={isSubmitting}>
            Create role
          </Button>
        </>
      }
    >
      <form
        id="create-admin-role-form"
        onSubmit={form.handleSubmit((values) => {
          onSubmit(values);
          form.reset();
        })}
        className="flex flex-col gap-4"
        noValidate
      >
        {error ? <Alert>{describeError(error)}</Alert> : null}

        <FormGrid columns={1}>
          <Field
            label="Name"
            required
            autoComplete="off"
            error={form.formState.errors.name?.message}
            {...form.register("name")}
          />
          <Textarea
            label="Description"
            error={form.formState.errors.description?.message}
            {...form.register("description")}
          />
        </FormGrid>
      </form>
    </Modal>
  );
}
