"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ErrorState } from "@/components/states";
import {
  Alert,
  Button,
  Card,
  EmptyState,
  IconButton,
  Select,
  Skeleton,
  useToast,
} from "@/components/ui";
import { listGeographyCities, listGeographyPincodes, listGeographyZones } from "@/lib/lookup-api";
import { getServiceAreas, updateServiceAreas } from "@/lib/profile-api";
import type { ServiceAreaInput } from "@/lib/profile-types";

const EMPTY_ROW: ServiceAreaInput = { cityId: "", zoneId: "", pincodeId: "" };
const NO_SPECIFIC_ZONE = "";
const NO_SPECIFIC_PINCODE = "";

/**
 * Service areas editor (docs/PROVIDER.md's Capability & Coverage domain,
 * `provider_service_area`): the cities/zones/pincodes a provider is willing to
 * work in. The API is a full-replace PUT, so this edits a local draft list and
 * only sends it on "Save changes".
 *
 * City, zone and pincode are chosen from real name dropdowns (task 205, backed
 * by `/api/v1/geography/{cities,zones,pincodes}`) rather than the hand-typed
 * GUIDs this screen originally shipped with. Zone and pincode both default to
 * "anywhere in this city", which is the common case and the one that should
 * take no taps.
 */
export function ServiceAreasSection() {
  const queryClient = useQueryClient();
  const toast = useToast();
  const [rows, setRows] = useState<ServiceAreaInput[]>([]);
  const [isDirty, setIsDirty] = useState(false);

  const query = useQuery({ queryKey: ["provider-service-areas"], queryFn: getServiceAreas });
  const citiesQuery = useQuery({ queryKey: ["geography-cities"], queryFn: listGeographyCities });
  // Fetched once, unfiltered, then filtered per-row client-side - simpler than
  // one query per row and the geography master is small enough that this is cheap.
  const zonesQuery = useQuery({ queryKey: ["geography-zones"], queryFn: () => listGeographyZones() });
  const pincodesQuery = useQuery({
    queryKey: ["geography-pincodes"],
    queryFn: () => listGeographyPincodes(),
  });

  // Adjusting state when a prop changes (react.dev/learn/you-might-not-need-an-effect).
  const [seededFor, setSeededFor] = useState({ data: query.data, isDirty });
  if (seededFor.data !== query.data || seededFor.isDirty !== isDirty) {
    setSeededFor({ data: query.data, isDirty });
    if (query.data && !isDirty) {
      setRows(
        query.data.map((area) => ({
          cityId: area.cityId,
          zoneId: area.zoneId ?? "",
          pincodeId: area.pincodeId ?? "",
        })),
      );
    }
  }

  const mutation = useMutation({
    mutationFn: (areas: ServiceAreaInput[]) => updateServiceAreas({ areas }),
    onSuccess: (areas) => {
      queryClient.setQueryData(["provider-service-areas"], areas);
      setIsDirty(false);
      toast("success", "Service areas saved.");
    },
  });

  function updateRow(index: number, patch: Partial<ServiceAreaInput>) {
    setIsDirty(true);
    setRows((current) => current.map((row, i) => (i === index ? { ...row, ...patch } : row)));
  }

  function removeRow(index: number) {
    setIsDirty(true);
    setRows((current) => current.filter((_, i) => i !== index));
  }

  function addRow() {
    setIsDirty(true);
    setRows((current) => [...current, { ...EMPTY_ROW }]);
  }

  function save() {
    const areas = rows
      .filter((row) => row.cityId.trim() !== "")
      .map((row) => ({
        cityId: row.cityId.trim(),
        zoneId: row.zoneId?.trim() || undefined,
        pincodeId: row.pincodeId?.trim() || undefined,
      }));
    mutation.mutate(areas);
  }

  if (
    query.isPending ||
    citiesQuery.isPending ||
    zonesQuery.isPending ||
    pincodesQuery.isPending
  ) {
    return (
      <Card title="Service areas">
        <div className="flex flex-col gap-2.5" aria-hidden>
          {Array.from({ length: 2 }, (_, index) => (
            <Skeleton key={index} className="h-28 w-full rounded-xl" />
          ))}
        </div>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Service areas">
        <ErrorState
          title="Couldn't load your service areas"
          error={query.error}
          onRetry={() => query.refetch()}
          isRetrying={query.isRefetching}
        />
      </Card>
    );
  }

  if (citiesQuery.isError || zonesQuery.isError || pincodesQuery.isError) {
    return (
      <Card title="Service areas">
        <ErrorState
          title="Couldn't load the list of places"
          error={citiesQuery.error ?? zonesQuery.error ?? pincodesQuery.error}
          onRetry={() => {
            citiesQuery.refetch();
            zonesQuery.refetch();
            pincodesQuery.refetch();
          }}
          isRetrying={
            citiesQuery.isRefetching || zonesQuery.isRefetching || pincodesQuery.isRefetching
          }
        />
      </Card>
    );
  }

  const cityOptions = citiesQuery.data.map((city) => ({ value: city.id, label: city.name }));

  // A row with no city is dropped silently on save, which reads as the save
  // having failed. Block the save and say so instead.
  const incompleteCount = rows.filter((row) => row.cityId.trim() === "").length;

  return (
    <Card
      title="Service areas"
      description="Cities, zones and pincodes you're willing to work in."
      actions={
        rows.length > 0 ? (
          <Button variant="secondary" size="sm" onClick={addRow}>
            Add area
          </Button>
        ) : null
      }
    >
      <div className="flex flex-col gap-4">
        {mutation.isError ? (
          <ErrorState title="Couldn't save your service areas" error={mutation.error} />
        ) : null}

        {rows.length === 0 ? (
          <EmptyState
            title="No service areas yet"
            description="Add at least one city so jobs near you can be assigned to you."
            action={
              <Button variant="secondary" onClick={addRow}>
                Add your first area
              </Button>
            }
          />
        ) : (
          <div className="flex flex-col gap-2.5">
            {rows.map((row, index) => {
              const zoneOptions = [
                { value: NO_SPECIFIC_ZONE, label: "Any zone in this city" },
                ...zonesQuery.data
                  .filter((zone) => zone.cityId === row.cityId)
                  .map((zone) => ({ value: zone.id, label: zone.name })),
              ];
              const pincodeOptions = [
                { value: NO_SPECIFIC_PINCODE, label: "Any pincode in this city" },
                ...pincodesQuery.data
                  .filter((pincode) => pincode.cityId === row.cityId)
                  .map((pincode) => ({ value: pincode.id, label: pincode.code })),
              ];

              return (
                <div key={index} className="rounded-xl border border-line bg-surface-2 p-3.5">
                  <div className="flex items-start justify-between gap-3">
                    <p className="text-xs font-semibold uppercase tracking-wide text-fg-muted">
                      Area {index + 1}
                    </p>
                    <IconButton
                      label={`Remove area ${index + 1}`}
                      onClick={() => removeRow(index)}
                      className="-mr-1.5 -mt-1.5 shrink-0"
                    >
                      <svg
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        strokeWidth="2"
                        strokeLinecap="round"
                        className="h-4 w-4"
                        aria-hidden
                      >
                        <path d="M18 6 6 18M6 6l12 12" />
                      </svg>
                    </IconButton>
                  </div>

                  <div className="mt-2 grid gap-3 sm:grid-cols-3">
                    <Select
                      id={`area-city-${index}`}
                      label="City"
                      placeholder="Select a city…"
                      options={cityOptions}
                      value={row.cityId}
                      onChange={(e) =>
                        updateRow(index, { cityId: e.target.value, zoneId: "", pincodeId: "" })
                      }
                    />
                    <Select
                      id={`area-zone-${index}`}
                      label="Zone"
                      hint={!row.cityId ? "Pick a city first." : undefined}
                      options={zoneOptions}
                      value={row.zoneId ?? NO_SPECIFIC_ZONE}
                      disabled={!row.cityId}
                      onChange={(e) => updateRow(index, { zoneId: e.target.value })}
                    />
                    <Select
                      id={`area-pincode-${index}`}
                      label="Pincode"
                      hint={!row.cityId ? "Pick a city first." : undefined}
                      options={pincodeOptions}
                      value={row.pincodeId ?? NO_SPECIFIC_PINCODE}
                      disabled={!row.cityId}
                      onChange={(e) => updateRow(index, { pincodeId: e.target.value })}
                    />
                  </div>
                </div>
              );
            })}
          </div>
        )}

        {incompleteCount > 0 ? (
          <Alert tone="warning">
            {incompleteCount === 1
              ? "One area has no city selected. Choose one, or remove the row, to save."
              : `${incompleteCount} areas have no city selected. Choose them, or remove the rows, to save.`}
          </Alert>
        ) : null}

        <div className="flex items-center justify-between gap-3 border-t border-line pt-4">
          <p className="text-sm text-fg-muted" aria-live="polite">
            {isDirty ? "You have unsaved changes." : "All changes saved."}
          </p>
          <Button
            type="button"
            loading={mutation.isPending}
            disabled={!isDirty || incompleteCount > 0}
            onClick={save}
          >
            Save changes
          </Button>
        </div>
      </div>
    </Card>
  );
}
