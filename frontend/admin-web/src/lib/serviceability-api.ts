/**
 * Typed client for the Admin API's serviceability surface (SRS 12.9, task
 * 111/112): geography master CRUD (`GeographyController`) and category/city +
 * service/pincode serviceability mapping (`ServiceabilityMappingsController`).
 * Every call is authenticated - these are admin-only endpoints gated behind
 * the "serviceability" permission module server-side.
 */
import { API_V1, apiFetch } from "./api";
import type {
  CategoryCityMappingCreateRequest,
  CategoryCityMappingResponse,
  CategoryLookupResponse,
  CityAdminResponse,
  CityCreateRequest,
  CityUpdateRequest,
  LocalityAdminResponse,
  LocalityCreateRequest,
  LocalityUpdateRequest,
  MappedPincodeWithoutProviderCoverageResponse,
  PincodeAdminResponse,
  PincodeCreateRequest,
  ServiceLookupResponse,
  ServiceabilityCoverageGapResponse,
  ServicePincodeMappingCreateRequest,
  ServicePincodeMappingResponse,
  StateCreateRequest,
  StateResponse,
  StateUpdateRequest,
  UnmappedActiveServiceResponse,
  ZoneResponse,
  ZoneCreateRequest,
  ZoneUpdateRequest,
} from "./serviceability-types";

const GEOGRAPHY_BASE = `${API_V1}/geography`;
const MAPPINGS_BASE = `${API_V1}/serviceability-mappings`;

function query(params: Record<string, string | undefined>): string {
  const entries = Object.entries(params).filter(([, value]) => value !== undefined);
  if (entries.length === 0) return "";
  return `?${new URLSearchParams(entries as [string, string][]).toString()}`;
}

// ---- States ----

export const listStates = () =>
  apiFetch<StateResponse[]>(`${GEOGRAPHY_BASE}/states`, { authenticated: true });

export const createState = (request: StateCreateRequest) =>
  apiFetch<StateResponse>(`${GEOGRAPHY_BASE}/states`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updateState = (id: string, request: StateUpdateRequest) =>
  apiFetch<StateResponse>(`${GEOGRAPHY_BASE}/states/${id}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setStateActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${GEOGRAPHY_BASE}/states/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

// ---- Cities ----

export const listCities = (stateId?: string) =>
  apiFetch<CityAdminResponse[]>(`${GEOGRAPHY_BASE}/cities${query({ stateId })}`, { authenticated: true });

export const createCity = (request: CityCreateRequest) =>
  apiFetch<CityAdminResponse>(`${GEOGRAPHY_BASE}/cities`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updateCity = (id: string, request: CityUpdateRequest) =>
  apiFetch<CityAdminResponse>(`${GEOGRAPHY_BASE}/cities/${id}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setCityActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${GEOGRAPHY_BASE}/cities/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

// ---- Zones ----

export const listZones = (cityId?: string) =>
  apiFetch<ZoneResponse[]>(`${GEOGRAPHY_BASE}/zones${query({ cityId })}`, { authenticated: true });

export const createZone = (request: ZoneCreateRequest) =>
  apiFetch<ZoneResponse>(`${GEOGRAPHY_BASE}/zones`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updateZone = (id: string, request: ZoneUpdateRequest) =>
  apiFetch<ZoneResponse>(`${GEOGRAPHY_BASE}/zones/${id}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setZoneActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${GEOGRAPHY_BASE}/zones/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

// ---- Localities ----

export const listLocalities = (zoneId?: string) =>
  apiFetch<LocalityAdminResponse[]>(`${GEOGRAPHY_BASE}/localities${query({ zoneId })}`, { authenticated: true });

export const createLocality = (request: LocalityCreateRequest) =>
  apiFetch<LocalityAdminResponse>(`${GEOGRAPHY_BASE}/localities`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updateLocality = (id: string, request: LocalityUpdateRequest) =>
  apiFetch<LocalityAdminResponse>(`${GEOGRAPHY_BASE}/localities/${id}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setLocalityActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${GEOGRAPHY_BASE}/localities/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

// ---- Pincodes ----

export const listPincodes = (cityId?: string) =>
  apiFetch<PincodeAdminResponse[]>(`${GEOGRAPHY_BASE}/pincodes${query({ cityId })}`, { authenticated: true });

export const createPincode = (request: PincodeCreateRequest) =>
  apiFetch<PincodeAdminResponse>(`${GEOGRAPHY_BASE}/pincodes`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setPincodeActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${GEOGRAPHY_BASE}/pincodes/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

// ---- Lookups ----

export const listCategoryLookups = () =>
  apiFetch<CategoryLookupResponse[]>(`${MAPPINGS_BASE}/categories`, { authenticated: true });

export const listServiceLookups = () =>
  apiFetch<ServiceLookupResponse[]>(`${MAPPINGS_BASE}/services`, { authenticated: true });

// ---- Category <-> City mappings ----

export const listCategoryCityMappings = (categoryId?: string, cityId?: string) =>
  apiFetch<CategoryCityMappingResponse[]>(`${MAPPINGS_BASE}/category-city${query({ categoryId, cityId })}`, {
    authenticated: true,
  });

export const createCategoryCityMapping = (request: CategoryCityMappingCreateRequest) =>
  apiFetch<CategoryCityMappingResponse>(`${MAPPINGS_BASE}/category-city`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setCategoryCityMappingActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${MAPPINGS_BASE}/category-city/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

// ---- Service <-> Pincode mappings ----

export const listServicePincodeMappings = (serviceId?: string, pincodeId?: string) =>
  apiFetch<ServicePincodeMappingResponse[]>(`${MAPPINGS_BASE}/service-pincode${query({ serviceId, pincodeId })}`, {
    authenticated: true,
  });

export const createServicePincodeMapping = (request: ServicePincodeMappingCreateRequest) =>
  apiFetch<ServicePincodeMappingResponse>(`${MAPPINGS_BASE}/service-pincode`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setServicePincodeMappingActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${MAPPINGS_BASE}/service-pincode/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

// ---- Coverage gap map (docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Coverage gap map") ----

/** Active services with no active pincode mapping anywhere. */
export const listUnmappedActiveServices = () =>
  apiFetch<UnmappedActiveServiceResponse[]>(`${MAPPINGS_BASE}/unmapped-active-services`, { authenticated: true });

/** (Service, pincode) pairs with active provider coverage but no active mapping. */
export const listServiceabilityCoverageGaps = () =>
  apiFetch<ServiceabilityCoverageGapResponse[]>(`${MAPPINGS_BASE}/coverage-gaps`, { authenticated: true });

/** Active mappings with no active provider able to fulfil them. */
export const listMappedPincodesWithoutProviderCoverage = () =>
  apiFetch<MappedPincodeWithoutProviderCoverageResponse[]>(`${MAPPINGS_BASE}/mapped-without-coverage`, {
    authenticated: true,
  });
