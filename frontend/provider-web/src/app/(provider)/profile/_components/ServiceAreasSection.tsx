"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Alert, Button, Card, Select } from "@/components/ui";
import { describeError } from "@/lib/api";
import { listGeographyCities, listGeographyPincodes, listGeographyZones } from "@/lib/lookup-api";
import { getServiceAreas, updateServiceAreas } from "@/lib/profile-api";
import type { ServiceAreaInput } from "@/lib/profile-types";

const EMPTY_ROW: ServiceAreaInput = { cityId: "", zoneId: "", pincodeId: "" };
const NO_SPECIFIC_ZONE = "";
const NO_SPECIFIC_PINCODE = "";

/**
 * Service areas editor (docs/PROVIDER.md's Capability & Coverage domain,
 * `provider_service_area`): the cities/zones/pincodes a provider is willing to
 * work in. The API is a full-replace PUT, so this section edits a local
 * draft list and only sends it on "Save changes".
 *
 * City/zone/pincode are chosen from real name dropdowns (task 205, backed by
 * `/api/v1/geography/{cities,zones,pincodes}`) rather than the hand-typed
 * GUIDs this screen originally shipped with - there was no serviceability
 * lookup endpoint on provider-api at the time.
 */
export function ServiceAreasSection() {
  const queryClient = useQueryClient();
  const [rows, setRows] = useState<ServiceAreaInput[]>([]);
  const [isDirty, setIsDirty] = useState(false);

  const query = useQuery({ queryKey: ["provider-service-areas"], queryFn: getServiceAreas });
  const citiesQuery = useQuery({ queryKey: ["geography-cities"], queryFn: listGeographyCities });
  // Fetched once, unfiltered, then filtered per-row client-side - simpler than
  // one query per row and the geography master is small enough that this is cheap.
  const zonesQuery = useQuery({ queryKey: ["geography-zones"], queryFn: () => listGeographyZones() });
  const pincodesQuery = useQuery({ queryKey: ["geography-pincodes"], queryFn: () => listGeographyPincodes() });

  useEffect(() => {
    if (query.data && !isDirty) {
      setRows(
        query.data.map((area) => ({
          cityId: area.cityId,
          zoneId: area.zoneId ?? "",
          pincodeId: area.pincodeId ?? "",
        })),
      );
    }
  }, [query.data, isDirty]);

  const mutation = useMutation({
    mutationFn: (areas: ServiceAreaInput[]) => updateServiceAreas({ areas }),
    onSuccess: (areas) => {
      queryClient.setQueryData(["provider-service-areas"], areas);
      setIsDirty(false);
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

  if (query.isPending || citiesQuery.isPending || zonesQuery.isPending || pincodesQuery.isPending) {
    return (
      <Card title="Service areas">
        <p className="text-sm text-neutral-500">Loading service areas…</p>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Service areas">
        <Alert>{describeError(query.error)}</Alert>
      </Card>
    );
  }

  if (citiesQuery.isError || zonesQuery.isError || pincodesQuery.isError) {
    return (
      <Card title="Service areas">
        <Alert>{describeError(citiesQuery.error ?? zonesQuery.error ?? pincodesQuery.error)}</Alert>
      </Card>
    );
  }

  const cityOptions = citiesQuery.data.map((city) => ({ value: city.id, label: city.name }));

  return (
    <Card title="Service areas" description="Cities, zones and pincodes you're willing to work in.">
      {mutation.isError ? (
        <div className="mb-3">
          <Alert>{describeError(mutation.error)}</Alert>
        </div>
      ) : null}

      {rows.length === 0 ? (
        <p className="mb-3 text-sm text-neutral-600 dark:text-neutral-400">No service areas added yet.</p>
      ) : (
        <div className="flex flex-col gap-3">
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
              <div key={index} className="flex flex-wrap items-end gap-3">
                <div className="w-48">
                  <Select
                    id={`area-city-${index}`}
                    label="City"
                    placeholder="Select a city…"
                    options={cityOptions}
                    value={row.cityId}
                    onChange={(e) => updateRow(index, { cityId: e.target.value, zoneId: "", pincodeId: "" })}
                  />
                </div>
                <div className="w-48">
                  <Select
                    id={`area-zone-${index}`}
                    label="Zone"
                    options={zoneOptions}
                    value={row.zoneId ?? NO_SPECIFIC_ZONE}
                    disabled={!row.cityId}
                    onChange={(e) => updateRow(index, { zoneId: e.target.value })}
                  />
                </div>
                <div className="w-48">
                  <Select
                    id={`area-pincode-${index}`}
                    label="Pincode"
                    options={pincodeOptions}
                    value={row.pincodeId ?? NO_SPECIFIC_PINCODE}
                    disabled={!row.cityId}
                    onChange={(e) => updateRow(index, { pincodeId: e.target.value })}
                  />
                </div>
                <Button type="button" variant="danger" onClick={() => removeRow(index)}>
                  Remove
                </Button>
              </div>
            );
          })}
        </div>
      )}

      <div className="mt-4 flex gap-2">
        <Button type="button" variant="secondary" onClick={addRow}>
          Add area
        </Button>
        <Button type="button" disabled={mutation.isPending || !isDirty} onClick={save}>
          {mutation.isPending ? "Saving…" : "Save changes"}
        </Button>
      </div>
    </Card>
  );
}
