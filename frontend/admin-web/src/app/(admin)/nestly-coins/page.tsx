"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { RequireAdminAuth } from "@/components/RequireAdminAuth";
import { Alert, Button, Card, Field, PageHeading } from "@/components/ui";
import { API_V1, ApiError, apiFetch, describeError } from "@/lib/api";
import { getSessionClaims, subscribeToAuthChanges } from "@/lib/auth";
import { canWriteModule } from "@/lib/permissions";
import type { AdminSessionClaims } from "@/lib/types";
import { isoDateOffsetFromToday, todayIsoDate } from "@/lib/date";

enum NestlyCoinsAudience {
  Customer = 0,
  Provider = 1,
}

const AUDIENCE_OPTIONS = [
  { value: String(NestlyCoinsAudience.Customer), label: "Customer" },
  { value: String(NestlyCoinsAudience.Provider), label: "Provider" },
] as const;

interface NestlyCoinsProgramConfigResponse {
  id: string;
  audience: NestlyCoinsAudience;
  earnRatePer100: number;
  minimumOrderAmount: number;
  requireReorder: boolean;
  maxCoinsPerMonth: number | null;
  expiryDays: number;
  clawbackWindowDays: number;
  isActive: boolean;
  updatedAtUtc: string;
}

interface NestlyCoinsReportResponse {
  audience: NestlyCoinsAudience;
  fromUtc: string;
  toUtc: string;
  totalIssued: number;
  totalClawedBack: number;
  netOutstanding: number;
}

const DEFAULT_CONFIG: Omit<NestlyCoinsProgramConfigResponse, "id" | "audience" | "updatedAtUtc"> = {
  earnRatePer100: 5,
  minimumOrderAmount: 200,
  requireReorder: true,
  maxCoinsPerMonth: null,
  expiryDays: 30,
  clawbackWindowDays: 3,
  isActive: false,
};

/**
 * Admin get/update for the per-audience Nestly Coins program config, plus
 * the coins-issued/clawed-back report (task 202). GET/PUT
 * /admin/nestly-coins/config/{audience}, GET /admin/nestly-coins/reports/issued.
 */
export default function NestlyCoinsPage() {
  return (
    <RequireAdminAuth>
      <NestlyCoinsScreen />
    </RequireAdminAuth>
  );
}

function NestlyCoinsScreen() {
  const [claims, setClaims] = useState<AdminSessionClaims | null>(null);
  useEffect(() => {
    const sync = () => setClaims(getSessionClaims());
    sync();
    return subscribeToAuthChanges(sync);
  }, []);
  const canWrite = canWriteModule(claims, "nestly-coins");

  const [audience, setAudience] = useState(NestlyCoinsAudience.Customer);

  const configQuery = useQuery({
    queryKey: ["nestly-coins-config", audience],
    queryFn: async () => {
      try {
        return await apiFetch<NestlyCoinsProgramConfigResponse>(
          `${API_V1}/nestly-coins/config/${audience}`,
          { authenticated: true },
        );
      } catch (err) {
        // Not configured yet is a normal, expected state (task 200/201's
        // services treat "no row" as "coins disabled for this side") - show
        // the create form pre-filled with defaults rather than an error.
        if (err instanceof ApiError && err.status === 404) return null;
        throw err;
      }
    },
  });

  return (
    <main className="flex flex-col gap-8">
      <PageHeading title="Nestly Coins" subtitle="Per-audience earn rate, reorder rule, cap, expiry and clawback window." />

      <div className="flex gap-2" role="tablist" aria-label="Audience">
        {AUDIENCE_OPTIONS.map((option) => (
          <Button
            key={option.value}
            role="tab"
            aria-selected={audience === Number(option.value)}
            variant={audience === Number(option.value) ? "primary" : "secondary"}
            onClick={() => setAudience(Number(option.value))}
          >
            {option.label}
          </Button>
        ))}
      </div>

      {configQuery.isPending ? (
        <p className="text-sm text-neutral-500">Loading…</p>
      ) : configQuery.isError ? (
        <Alert>{describeError(configQuery.error)}</Alert>
      ) : (
        <ConfigForm audience={audience} config={configQuery.data} canWrite={canWrite} />
      )}

      <ReportPanel audience={audience} />
    </main>
  );
}

function ConfigForm({
  audience,
  config,
  canWrite,
}: {
  audience: NestlyCoinsAudience;
  config: NestlyCoinsProgramConfigResponse | null;
  canWrite: boolean;
}) {
  const queryClient = useQueryClient();
  const [form, setForm] = useState(() => config ?? { audience, ...DEFAULT_CONFIG });

  useEffect(() => setForm(config ?? { audience, ...DEFAULT_CONFIG }), [config, audience]);

  const upsertMutation = useMutation({
    mutationFn: () =>
      apiFetch<NestlyCoinsProgramConfigResponse>(`${API_V1}/nestly-coins/config/${audience}`, {
        method: "PUT",
        authenticated: true,
        body: JSON.stringify({
          earnRatePer100: form.earnRatePer100,
          minimumOrderAmount: form.minimumOrderAmount,
          requireReorder: form.requireReorder,
          maxCoinsPerMonth: form.maxCoinsPerMonth,
          expiryDays: form.expiryDays,
          clawbackWindowDays: form.clawbackWindowDays,
          isActive: form.isActive,
        }),
      }),
    onSuccess: (updated) => {
      setForm(updated);
      queryClient.setQueryData(["nestly-coins-config", audience], updated);
    },
  });

  return (
    <Card
      title="Program config"
      description={config ? undefined : "Not configured yet for this audience - saving activates it."}
    >
      <form
        className="flex flex-col gap-4"
        onSubmit={(event) => {
          event.preventDefault();
          upsertMutation.mutate();
        }}
      >
        {upsertMutation.isError ? <Alert>{describeError(upsertMutation.error)}</Alert> : null}
        {upsertMutation.isSuccess ? <Alert tone="success">Saved.</Alert> : null}

        <div className="grid grid-cols-2 gap-4">
          <Field
            label="Earn rate per ₹100"
            type="number"
            step="0.01"
            disabled={!canWrite}
            value={form.earnRatePer100}
            onChange={(e) => setForm({ ...form, earnRatePer100: Number(e.target.value) })}
          />
          <Field
            label="Minimum order amount"
            type="number"
            step="0.01"
            disabled={!canWrite}
            value={form.minimumOrderAmount}
            onChange={(e) => setForm({ ...form, minimumOrderAmount: Number(e.target.value) })}
          />
          <Field
            label="Max coins per month (blank = unlimited)"
            type="number"
            step="0.01"
            disabled={!canWrite}
            value={form.maxCoinsPerMonth ?? ""}
            onChange={(e) => setForm({ ...form, maxCoinsPerMonth: e.target.value === "" ? null : Number(e.target.value) })}
          />
          <Field
            label="Expiry (days)"
            type="number"
            disabled={!canWrite}
            value={form.expiryDays}
            onChange={(e) => setForm({ ...form, expiryDays: Number(e.target.value) })}
          />
          <Field
            label="Clawback window (days)"
            type="number"
            disabled={!canWrite}
            value={form.clawbackWindowDays}
            onChange={(e) => setForm({ ...form, clawbackWindowDays: Number(e.target.value) })}
          />
        </div>

        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            disabled={!canWrite}
            checked={form.requireReorder}
            onChange={(e) => setForm({ ...form, requireReorder: e.target.checked })}
          />
          Require reorder (2nd+ completed order only)
        </label>

        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            disabled={!canWrite}
            checked={form.isActive}
            onChange={(e) => setForm({ ...form, isActive: e.target.checked })}
          />
          Program active
        </label>

        {canWrite ? (
          <Button type="submit" disabled={upsertMutation.isPending} className="w-fit">
            {upsertMutation.isPending ? "Saving…" : "Save"}
          </Button>
        ) : null}
      </form>
    </Card>
  );
}

function ReportPanel({ audience }: { audience: NestlyCoinsAudience }) {
  const today = todayIsoDate();
  // setDate-based, so it stays correct across a DST transition where a day
  // is not 24 hours — unlike subtracting 30 * 24h in milliseconds.
  const monthAgo = isoDateOffsetFromToday(-30);
  const [fromDate, setFromDate] = useState(monthAgo);
  const [toDate, setToDate] = useState(today);
  const [range, setRange] = useState({ from: monthAgo, to: today });

  const reportQuery = useQuery({
    queryKey: ["nestly-coins-report", audience, range.from, range.to],
    queryFn: () =>
      apiFetch<NestlyCoinsReportResponse>(
        `${API_V1}/nestly-coins/reports/issued?audience=${audience}&fromUtc=${range.from}&toUtc=${range.to}`,
        { authenticated: true },
      ),
  });

  return (
    <Card title="Coins issued vs. clawed back" description="Program cost over a date range.">
      <form
        className="mb-4 flex items-end gap-3"
        onSubmit={(event) => {
          event.preventDefault();
          setRange({ from: fromDate, to: toDate });
        }}
      >
        <Field label="From" type="date" value={fromDate} onChange={(e) => setFromDate(e.target.value)} />
        <Field label="To" type="date" value={toDate} onChange={(e) => setToDate(e.target.value)} />
        <Button type="submit">Load</Button>
      </form>

      {reportQuery.isPending ? (
        <p className="text-sm text-neutral-500">Loading…</p>
      ) : reportQuery.isError ? (
        <Alert>{describeError(reportQuery.error)}</Alert>
      ) : (
        <dl className="grid grid-cols-3 gap-4 text-sm">
          <div>
            <dt className="text-neutral-600 dark:text-neutral-400">Issued</dt>
            <dd className="text-lg font-semibold">₹{reportQuery.data.totalIssued.toFixed(2)}</dd>
          </div>
          <div>
            <dt className="text-neutral-600 dark:text-neutral-400">Clawed back</dt>
            <dd className="text-lg font-semibold">₹{reportQuery.data.totalClawedBack.toFixed(2)}</dd>
          </div>
          <div>
            <dt className="text-neutral-600 dark:text-neutral-400">Net outstanding</dt>
            <dd className="text-lg font-semibold">₹{reportQuery.data.netOutstanding.toFixed(2)}</dd>
          </div>
        </dl>
      )}
    </Card>
  );
}
