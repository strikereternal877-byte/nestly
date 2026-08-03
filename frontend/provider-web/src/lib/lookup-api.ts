/**
 * Typed client for the Provider API's read-only catalog/geography lookup
 * surface (`/api/v1/catalog`, `/api/v1/geography`) - task 205, backing the
 * skills and service-areas pickers with real names instead of hand-typed
 * GUIDs.
 */
import { API_V1, apiFetch } from "./api";
import type {
  CatalogCategory,
  CatalogService,
  GeographyCity,
  GeographyPincode,
  GeographyZone,
} from "./lookup-types";

export const listCatalogCategories = () =>
  apiFetch<CatalogCategory[]>(`${API_V1}/catalog/categories`, { authenticated: true });

export const listCatalogServices = (categoryId?: string) => {
  const query = categoryId ? `?categoryId=${encodeURIComponent(categoryId)}` : "";
  return apiFetch<CatalogService[]>(`${API_V1}/catalog/services${query}`, { authenticated: true });
};

export const listGeographyCities = () =>
  apiFetch<GeographyCity[]>(`${API_V1}/geography/cities`, { authenticated: true });

export const listGeographyZones = (cityId?: string) => {
  const query = cityId ? `?cityId=${encodeURIComponent(cityId)}` : "";
  return apiFetch<GeographyZone[]>(`${API_V1}/geography/zones${query}`, { authenticated: true });
};

export const listGeographyPincodes = (cityId?: string) => {
  const query = cityId ? `?cityId=${encodeURIComponent(cityId)}` : "";
  return apiFetch<GeographyPincode[]>(`${API_V1}/geography/pincodes${query}`, { authenticated: true });
};
