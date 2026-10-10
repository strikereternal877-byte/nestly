"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import Link from "next/link";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { z } from "zod";
import {
  DataTable,
  FilterBar,
  FormGrid,
  Pagination,
  countActiveFilters,
  formatDate,
} from "@/components/data-table";
import type { DataTableColumn } from "@/components/data-table";
import { Alert, Button, Field, Modal, PageHeading, Select, useToast } from "@/components/ui";
import { createAdminUser, listAdminRoles, searchAdminUsers } from "@/lib/admin-users-api";
import { AdminUserStatus } from "@/lib/admin-users-types";
import type { AdminUserSummary } from "@/lib/admin-users-types";
import { describeError } from "@/lib/api";
import { canWriteModule } from "@/lib/permissions";
import { useAdminClaims } from "@/lib/use-admin-claims";
import { AdminUserStatusBadge } from "./_components/AdminUserStatusBadge";

const PAGE_SIZE = 20;

const STATUS_FILTER_OPTIONS = [
  { value: "", label: "Any status" },
  { value: String(AdminUserStatus.Active), label: "Active" },
  { value: String(AdminUserStatus.Inactive), label: "Inactive" },
];

interface AdminUserFilters {
  name: string;
  email: string;
  status: string;
}

const EMPTY_FILTERS: AdminUserFilters = { name: "", email: "", status: "" };

const createSchema = z.object({
  fullName: z.string().trim().min(1, "Full name is required").max(200),
  email: z.string().trim().min(1, "Email is required").email("Enter a valid email address"),
  password: z.string().min(8, "Use at least 8 characters"),
  roleId: z.string(),
});
type CreateFormValues = z.infer<typeof createSchema>;

const EMPTY_CREATE_FORM: CreateFormValues = { fullName: "", email: "", password: "", roleId: "" };

/**
 * Admin user management (SRS 12.2.1, tasks 97a-97d): search/filter, create,
 * and a link into each account's detail page for role assignment,
 * activate/deactivate and password reset. Gated on the "settings" write
 * permission for the create form - AdminModules has no separate "admin-users"
 * module (see NAV_MODULES' admin-users entry, which is also keyed on
 * "settings.read"), so admin user management rides on the settings module's
 * grants. The API enforces this server-side regardless.
 */
export default function AdminUsersPage() {
  const claims = useAdminClaims();
  const canWrite = canWriteModule(claims, "settings");
  const queryClient = useQueryClient();
  const pushToast = useToast();

  const [filters, setFilters] = useState<AdminUserFilters>(EMPTY_FILTERS);
  const [page, setPage] = useState(1);
  const [isCreating, setIsCreating] = useState(false);

  const rolesQuery = useQuery({ queryKey: ["admin-user-roles"], queryFn: listAdminRoles });

  // Live filtering (no Search button): Name and Email are debounced 300ms
  // before they hit the query, same convention as customers/page.tsx; Status
  // (a dropdown) applies immediately. Name's debounce is shared with the
  // typeahead below rather than debounced twice.
  const [debouncedName, setDebouncedName] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedName(filters.name.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [filters.name]);

  const [debouncedEmail, setDebouncedEmail] = useState("");
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedEmail(filters.email.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [filters.email]);

  // Any filter change resets to page 1 (same pattern as customers/page.tsx).
  useResetOnChange([debouncedName, debouncedEmail, filters.status], () => setPage(1));

  const nameSuggestionsQuery = useQuery({
    queryKey: ["admin-users", "name-suggestions", debouncedName] as const,
    queryFn: () => searchAdminUsers({ name: debouncedName, page: 1, pageSize: 8 }),
    enabled: debouncedName.length >= 2,
    placeholderData: keepPreviousData,
  });

  const listQuery = useQuery({
    queryKey: ["admin-users", "search", debouncedName, debouncedEmail, filters.status, page] as const,
    queryFn: () =>
      searchAdminUsers({
        email: debouncedEmail || undefined,
        name: debouncedName || undefined,
        status: filters.status === "" ? undefined : (Number(filters.status) as AdminUserStatus),
        page,
        pageSize: PAGE_SIZE,
      }),
    // Paging used to blank the whole table between requests.
    placeholderData: keepPreviousData,
  });

  const createMutation = useMutation({
    mutationFn: (values: CreateFormValues) =>
      createAdminUser({
        email: values.email.trim(),
        fullName: values.fullName.trim(),
        password: values.password,
        roleId: values.roleId || null,
      }),
    onSuccess: (created) => {
      setIsCreating(false);
      queryClient.invalidateQueries({ queryKey: ["admin-users", "search"] });
      pushToast("success", `${created.fullName} can now sign in.`);
    },
  });

  const clearFilters = () => {
    setFilters(EMPTY_FILTERS);
    setDebouncedName("");
    setDebouncedEmail("");
    setPage(1);
  };

  // "No role assigned" is a real option, not `Select`'s `placeholder` - that
  // renders a *disabled* first option, so an account could never be created
  // (or later put back) without a role.
  const roleOptions = [
    { value: "", label: "No role assigned" },
    ...(rolesQuery.data ?? []).map((role) => ({ value: role.id, label: role.name })),
  ];
  const activeFilterCount = countActiveFilters(filters);

  const columns: DataTableColumn<AdminUserSummary>[] = [
    {
      key: "name",
      header: "Name",
      cell: (adminUser) => (
        <Link
          href={`/admin-users/${adminUser.id}`}
          className="font-medium text-fg underline-offset-4 transition-colors duration-fast ease-out hover:text-brand-600 hover:underline dark:hover:text-brand-400"
        >
          {adminUser.fullName}
        </Link>
      ),
    },
    { key: "email", header: "Email", cell: (adminUser) => adminUser.email },
    {
      key: "role",
      header: "Role",
      cell: (adminUser) => adminUser.roleName ?? <span className="text-fg-subtle">No role assigned</span>,
    },
    {
      key: "status",
      header: "Status",
      cell: (adminUser) => (
        <AdminUserStatusBadge status={adminUser.status} isLockedOut={adminUser.isLockedOut} />
      ),
    },
    {
      key: "created",
      header: "Created",
      cell: (adminUser) => <span className="nums whitespace-nowrap">{formatDate(adminUser.createdAtUtc)}</span>,
    },
  ];

  return (
    <div className="w-full max-w-6xl">
      <PageHeading
        title="Admin users"
        subtitle="Back-office operator accounts: create, assign roles, activate/deactivate and reset passwords (SRS 12.2.1)."
        actions={
          <>
            <Link
              href="/admin-users/roles"
              className="inline-flex h-10 items-center rounded-lg border border-line bg-surface px-4 text-sm font-medium text-fg shadow-xs transition-colors duration-fast ease-out hover:border-line-strong hover:bg-surface-2"
            >
              Roles & permissions
            </Link>
            {canWrite ? <Button onClick={() => setIsCreating(true)}>Create admin user</Button> : null}
          </>
        }
      />

      <div className="flex flex-col gap-6">
        <FilterBar
          columns={3}
          onClear={clearFilters}
          activeCount={activeFilterCount}
          busy={listQuery.isFetching}
        >
          <Field
            label="Name"
            name="name"
            autoComplete="name"
            list="admin-user-name-suggestions"
            placeholder="Search by name…"
            value={filters.name}
            onChange={(event) => setFilters((current) => ({ ...current, name: event.target.value }))}
          />
          <datalist id="admin-user-name-suggestions">
            {(nameSuggestionsQuery.data?.items ?? []).map((adminUser) => (
              <option key={adminUser.id} value={adminUser.fullName} />
            ))}
          </datalist>
          <Field
            label="Email"
            name="email"
            autoComplete="email"
            placeholder="Search by email…"
            value={filters.email}
            onChange={(event) => setFilters((current) => ({ ...current, email: event.target.value }))}
          />
          <Select
            label="Status"
            options={STATUS_FILTER_OPTIONS}
            value={filters.status}
            onChange={(event) => setFilters((current) => ({ ...current, status: event.target.value }))}
          />
        </FilterBar>

        <DataTable
          title="Admin users"
          description="Every back-office account, its role and its current state."
          columns={columns}
          rows={listQuery.data?.items}
          rowKey={(adminUser) => adminUser.id}
          isLoading={listQuery.isPending}
          isFetching={listQuery.isFetching}
          error={listQuery.error}
          onRetry={() => listQuery.refetch()}
          caption="Admin users matching the current filters"
          emptyTitle={
            activeFilterCount > 0 ? "No admin users match these filters" : "No admin users yet"
          }
          emptyDescription={
            activeFilterCount > 0
              ? "Try broadening the search."
              : canWrite
                ? "Create the first back-office account."
                : "An admin with write access to this module can create one."
          }
          emptyAction={
            activeFilterCount > 0 ? (
              <Button variant="secondary" onClick={clearFilters}>
                Clear filters
              </Button>
            ) : canWrite ? (
              <Button onClick={() => setIsCreating(true)}>Create admin user</Button>
            ) : undefined
          }
          skeletonRows={8}
          minWidth="880px"
          rowActions={(adminUser) => (
            <Link
              href={`/admin-users/${adminUser.id}`}
              aria-label={`Manage ${adminUser.fullName}`}
              className="inline-flex h-8 items-center rounded-lg px-3 text-xs font-medium text-fg-muted transition-colors duration-fast ease-out hover:bg-surface-3 hover:text-fg"
            >
              Manage
            </Link>
          )}
          footer={
            listQuery.data ? (
              <Pagination
                page={page}
                pageSize={PAGE_SIZE}
                totalCount={listQuery.data.totalCount}
                onPageChange={setPage}
                busy={listQuery.isFetching}
                itemLabel="admin user"
              />
            ) : null
          }
        />
      </div>

      <CreateAdminUserModal
        open={isCreating}
        roleOptions={roleOptions}
        rolesError={rolesQuery.error}
        onRetryRoles={() => rolesQuery.refetch()}
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

function CreateAdminUserModal({
  open,
  roleOptions,
  rolesError,
  onRetryRoles,
  isSubmitting,
  error,
  onSubmit,
  onClose,
}: {
  open: boolean;
  roleOptions: readonly { value: string; label: string }[];
  rolesError: unknown;
  onRetryRoles: () => void;
  isSubmitting: boolean;
  error: unknown;
  onSubmit: (values: CreateFormValues) => void;
  onClose: () => void;
}) {
  const form = useForm<CreateFormValues>({
    resolver: zodResolver(createSchema),
    defaultValues: EMPTY_CREATE_FORM,
  });

  // The dialog stays mounted while closed, so without this a second "Create
  // admin user" would open holding the previous attempt's password.
  useEffect(() => {
    if (open) form.reset(EMPTY_CREATE_FORM);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Create admin user"
      description="Provisions a new back-office account. The operator signs in with the temporary password you set here."
      footer={
        <>
          <Button type="button" variant="secondary" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </Button>
          <Button type="submit" form="create-admin-user-form" loading={isSubmitting}>
            Create admin user
          </Button>
        </>
      }
    >
      <form
        id="create-admin-user-form"
        onSubmit={form.handleSubmit(onSubmit)}
        className="flex flex-col gap-4"
        noValidate
      >
        {error ? <Alert>{describeError(error)}</Alert> : null}
        {rolesError ? (
          <Alert
            tone="warning"
            action={
              <Button size="sm" variant="secondary" onClick={onRetryRoles}>
                Retry
              </Button>
            }
          >
            Roles could not be loaded, so this account can only be created without one. It can be
            assigned from the account&apos;s own screen afterwards. {describeError(rolesError)}
          </Alert>
        ) : null}

        <FormGrid>
          <Field
            label="Full name"
            required
            autoComplete="off"
            error={form.formState.errors.fullName?.message}
            {...form.register("fullName")}
          />
          <Field
            label="Email"
            type="email"
            required
            autoComplete="off"
            error={form.formState.errors.email?.message}
            {...form.register("email")}
          />
          <Field
            label="Temporary password"
            type="password"
            required
            autoComplete="new-password"
            hint="At least 8 characters. Relay it to the account owner securely."
            error={form.formState.errors.password?.message}
            {...form.register("password")}
          />
          <Select
            label="Role"
            options={roleOptions}
            error={form.formState.errors.roleId?.message}
            {...form.register("roleId")}
          />
        </FormGrid>
      </form>
    </Modal>
  );
}
