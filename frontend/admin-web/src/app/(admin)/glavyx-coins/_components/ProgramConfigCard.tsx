"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Alert, Button, Card, CheckboxField, Field, useToast } from "@/components/ui";
import { FormActions, FormGrid } from "@/components/data-table";
import { describeError } from "@/lib/api";
import {
  NestlyCoinsAudience,
  upsertCoinsConfig,
  type NestlyCoinsProgramConfig,
  type NestlyCoinsProgramConfigRequest,
} from "../_lib/coins-api";

/** Starting point for an audience that has never been configured. */
const DEFAULT_CONFIG: NestlyCoinsProgramConfigRequest = {
  earnRatePer100: 5,
  minimumOrderAmount: 200,
  requireReorder: true,
  maxCoinsPerMonth: null,
  expiryDays: 30,
  clawbackWindowDays: 3,
  isActive: false,
};

function toFormValues(config: NestlyCoinsProgramConfig | null): NestlyCoinsProgramConfigRequest {
  if (!config) return DEFAULT_CONFIG;
  return {
    earnRatePer100: config.earnRatePer100,
    minimumOrderAmount: config.minimumOrderAmount,
    requireReorder: config.requireReorder,
    maxCoinsPerMonth: config.maxCoinsPerMonth,
    expiryDays: config.expiryDays,
    clawbackWindowDays: config.clawbackWindowDays,
    isActive: config.isActive,
  };
}

/**
 * The per-audience program config form (task 202). Every field maps to one
 * rule in NESTLY-COINS.md: earn rate, eligibility floor, reorder rule,
 * monthly cap, expiry and the clawback window.
 */
export function ProgramConfigCard({
  audience,
  config,
  canWrite,
}: {
  audience: NestlyCoinsAudience;
  config: NestlyCoinsProgramConfig | null;
  canWrite: boolean;
}) {
  const queryClient = useQueryClient();
  const toast = useToast();
  const [form, setForm] = useState<NestlyCoinsProgramConfigRequest>(() => toFormValues(config));

  // Re-seed when the audience tab changes or a refetch brings new values.
  useEffect(() => setForm(toFormValues(config)), [config, audience]);

  const saveMutation = useMutation({
    mutationFn: () => upsertCoinsConfig(audience, form),
    onSuccess: (updated) => {
      setForm(toFormValues(updated));
      // Seed the cache directly so the tab does not flash its previous values
      // between the save landing and the next refetch.
      queryClient.setQueryData(["nestly-coins-config", audience], updated);
      // A toast rather than an inline "Saved." alert: the old alert was tied
      // to the mutation's `isSuccess` and stayed on screen while the admin
      // edited the *other* audience, claiming a save that had not happened.
      toast("success", "Program config saved.");
    },
  });

  return (
    <Card
      title="Program config"
      description={
        config
          ? "Changes take effect on the next qualifying order for this audience."
          : "Not configured yet for this audience — saving creates the program."
      }
    >
      <form
        className="flex flex-col gap-5"
        onSubmit={(event) => {
          event.preventDefault();
          saveMutation.mutate();
        }}
      >
        {/* The form keeps its values on failure, so the admin can correct and
            resubmit rather than retyping the whole config. */}
        {saveMutation.isError ? <Alert>{describeError(saveMutation.error)}</Alert> : null}

        <FormGrid columns={2}>
          <Field
            label="Earn rate per ₹100"
            type="number"
            step="0.01"
            min={0}
            leading="₹"
            disabled={!canWrite}
            value={form.earnRatePer100}
            onChange={(event) => setForm({ ...form, earnRatePer100: Number(event.target.value) })}
          />
          <Field
            label="Minimum order amount"
            type="number"
            step="0.01"
            min={0}
            leading="₹"
            hint="Orders below this earn nothing."
            disabled={!canWrite}
            value={form.minimumOrderAmount}
            onChange={(event) => setForm({ ...form, minimumOrderAmount: Number(event.target.value) })}
          />
          <Field
            label="Max coins per month"
            type="number"
            step="0.01"
            min={0}
            hint="Blank means no monthly cap."
            disabled={!canWrite}
            value={form.maxCoinsPerMonth ?? ""}
            onChange={(event) =>
              setForm({
                ...form,
                maxCoinsPerMonth: event.target.value === "" ? null : Number(event.target.value),
              })
            }
          />
          <Field
            label="Expiry (days)"
            type="number"
            min={1}
            hint="How long an earned coin stays spendable."
            disabled={!canWrite}
            value={form.expiryDays}
            onChange={(event) => setForm({ ...form, expiryDays: Number(event.target.value) })}
          />
          <Field
            label="Clawback window (days)"
            type="number"
            min={0}
            hint="Coins are reversed if the order is cancelled or refunded inside this window."
            disabled={!canWrite}
            value={form.clawbackWindowDays}
            onChange={(event) => setForm({ ...form, clawbackWindowDays: Number(event.target.value) })}
          />
        </FormGrid>

        <div className="flex flex-col gap-3">
          <CheckboxField
            label="Require reorder"
            description="Only a customer's second and later completed orders earn coins."
            checked={form.requireReorder}
            disabled={!canWrite}
            onChange={(checked) => setForm({ ...form, requireReorder: checked })}
          />
          <CheckboxField
            label="Program active"
            description="Turning this off stops all earning for this audience immediately; existing balances are unaffected."
            checked={form.isActive}
            disabled={!canWrite}
            onChange={(checked) => setForm({ ...form, isActive: checked })}
          />
        </div>

        {canWrite ? (
          <FormActions>
            <Button type="submit" loading={saveMutation.isPending}>
              Save config
            </Button>
          </FormActions>
        ) : (
          <p className="text-sm text-fg-muted">
            You have read-only access to this module, so these values cannot be changed here.
          </p>
        )}
      </form>
    </Card>
  );
}
