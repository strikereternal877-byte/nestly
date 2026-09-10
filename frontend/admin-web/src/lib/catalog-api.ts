/**
 * Typed client for the Admin API's catalog management surface (SRS 12.5-12.7,
 * tasks 103a-107): `CategoriesController`, `ServicesController`,
 * `ServiceAddOnsController`. Every call is authenticated - these are
 * admin-only endpoints gated behind the "catalog" permission module
 * server-side (SRS 12.5-12.7 share one module).
 */
import { API_V1, apiFetch } from "./api";
import type {
  CatalogHealthIssueResponse,
  CategoryCreateRequest,
  CategoryGroupAdminResponse,
  CategoryGroupCreateRequest,
  CategoryGroupUpdateRequest,
  CategoryResponse,
  CategoryUpdateRequest,
  ServiceAddOnAdminResponse,
  ServiceAddOnCreateRequest,
  ServiceAddOnGroupAdminResponse,
  ServiceAddOnGroupCreateRequest,
  ServiceAddOnGroupUpdateRequest,
  ServiceAddOnUpdateRequest,
  ServiceAdminResponse,
  ServiceCreateRequest,
  ServiceGroupAdminResponse,
  ServiceGroupCreateRequest,
  ServiceGroupUpdateRequest,
  ServiceMediaCreateRequest,
  ServiceMediaResponse,
  ServiceUpdateRequest,
  ServiceVariantAdminResponse,
  ServiceVariantCreateRequest,
  ServiceVariantUpdateRequest,
} from "./catalog-types";

const CATEGORIES_BASE = `${API_V1}/catalog/categories`;
const SERVICES_BASE = `${API_V1}/catalog/services`;
const ADDONS_BASE = `${API_V1}/catalog/addons`;
const ADDON_GROUPS_BASE = `${API_V1}/catalog/addon-groups`;
const SERVICE_GROUPS_BASE = `${API_V1}/catalog/service-groups`;
const CATEGORY_GROUPS_BASE = `${API_V1}/catalog/category-groups`;

function query(params: Record<string, string | undefined>): string {
  const entries = Object.entries(params).filter(([, value]) => value !== undefined);
  if (entries.length === 0) return "";
  return `?${new URLSearchParams(entries as [string, string][]).toString()}`;
}

// ---- Categories ----

export const listCategories = () =>
  apiFetch<CategoryResponse[]>(CATEGORIES_BASE, { authenticated: true });

export const getCategory = (id: string) =>
  apiFetch<CategoryResponse>(`${CATEGORIES_BASE}/${id}`, { authenticated: true });

export const createCategory = (request: CategoryCreateRequest) =>
  apiFetch<CategoryResponse>(CATEGORIES_BASE, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updateCategory = (id: string, request: CategoryUpdateRequest) =>
  apiFetch<CategoryResponse>(`${CATEGORIES_BASE}/${id}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setCategoryActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${CATEGORIES_BASE}/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

export const setCategoryFeatured = (id: string, isFeatured: boolean) =>
  apiFetch<void>(`${CATEGORIES_BASE}/${id}/${isFeatured ? "feature" : "unfeature"}`, {
    method: "POST",
    authenticated: true,
  });

export const listCategoryChildren = (id: string) =>
  apiFetch<CategoryResponse[]>(`${CATEGORIES_BASE}/${id}/children`, { authenticated: true });

// ---- Services / packages ----

export const listServices = (categoryId?: string) =>
  apiFetch<ServiceAdminResponse[]>(`${SERVICES_BASE}${query({ categoryId })}`, { authenticated: true });

export const getService = (id: string) =>
  apiFetch<ServiceAdminResponse>(`${SERVICES_BASE}/${id}`, { authenticated: true });

export const createService = (request: ServiceCreateRequest) =>
  apiFetch<ServiceAdminResponse>(SERVICES_BASE, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updateService = (id: string, request: ServiceUpdateRequest) =>
  apiFetch<ServiceAdminResponse>(`${SERVICES_BASE}/${id}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setServiceActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${SERVICES_BASE}/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

export const setServiceFeatured = (id: string, isFeatured: boolean) =>
  apiFetch<void>(`${SERVICES_BASE}/${id}/${isFeatured ? "feature" : "unfeature"}`, {
    method: "POST",
    authenticated: true,
  });

export const listServiceMedia = (serviceId: string) =>
  apiFetch<ServiceMediaResponse[]>(`${SERVICES_BASE}/${serviceId}/media`, { authenticated: true });

export const addServiceMedia = (serviceId: string, request: ServiceMediaCreateRequest) =>
  apiFetch<ServiceMediaResponse>(`${SERVICES_BASE}/${serviceId}/media`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const removeServiceMedia = (serviceId: string, mediaId: string) =>
  apiFetch<void>(`${SERVICES_BASE}/${serviceId}/media/${mediaId}`, {
    method: "DELETE",
    authenticated: true,
  });

// ---- Catalog health (docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Catalog health") ----

/** Active services missing a price, an image, a serviceability mapping, or that have never been booked — warning/audit only, not a block. */
export const listCatalogHealthIssues = () =>
  apiFetch<CatalogHealthIssueResponse[]>(`${SERVICES_BASE}/health`, { authenticated: true });

// ---- Service variants (Phase 3 catalog redesign) ----

export const listServiceVariants = (serviceId: string) =>
  apiFetch<ServiceVariantAdminResponse[]>(`${SERVICES_BASE}/${serviceId}/variants`, { authenticated: true });

export const createServiceVariant = (serviceId: string, request: ServiceVariantCreateRequest) =>
  apiFetch<ServiceVariantAdminResponse>(`${SERVICES_BASE}/${serviceId}/variants`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updateServiceVariant = (serviceId: string, id: string, request: ServiceVariantUpdateRequest) =>
  apiFetch<ServiceVariantAdminResponse>(`${SERVICES_BASE}/${serviceId}/variants/${id}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setServiceVariantActive = (serviceId: string, id: string, isActive: boolean) =>
  apiFetch<void>(`${SERVICES_BASE}/${serviceId}/variants/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

export const deleteServiceVariant = (serviceId: string, id: string) =>
  apiFetch<void>(`${SERVICES_BASE}/${serviceId}/variants/${id}`, {
    method: "DELETE",
    authenticated: true,
  });

// ---- Add-ons ----

export const listServiceAddOns = (serviceId?: string) =>
  apiFetch<ServiceAddOnAdminResponse[]>(`${ADDONS_BASE}${query({ serviceId })}`, { authenticated: true });

export const getServiceAddOn = (id: string) =>
  apiFetch<ServiceAddOnAdminResponse>(`${ADDONS_BASE}/${id}`, { authenticated: true });

export const createServiceAddOn = (request: ServiceAddOnCreateRequest) =>
  apiFetch<ServiceAddOnAdminResponse>(ADDONS_BASE, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updateServiceAddOn = (id: string, request: ServiceAddOnUpdateRequest) =>
  apiFetch<ServiceAddOnAdminResponse>(`${ADDONS_BASE}/${id}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setServiceAddOnActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${ADDONS_BASE}/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

// ---- Add-on groups (Phase 3 catalog redesign) ----

export const listAddOnGroups = (serviceId?: string) =>
  apiFetch<ServiceAddOnGroupAdminResponse[]>(`${ADDON_GROUPS_BASE}${query({ serviceId })}`, { authenticated: true });

export const getAddOnGroup = (id: string) =>
  apiFetch<ServiceAddOnGroupAdminResponse>(`${ADDON_GROUPS_BASE}/${id}`, { authenticated: true });

export const createAddOnGroup = (request: ServiceAddOnGroupCreateRequest) =>
  apiFetch<ServiceAddOnGroupAdminResponse>(ADDON_GROUPS_BASE, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updateAddOnGroup = (id: string, request: ServiceAddOnGroupUpdateRequest) =>
  apiFetch<ServiceAddOnGroupAdminResponse>(`${ADDON_GROUPS_BASE}/${id}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const deleteAddOnGroup = (id: string) =>
  apiFetch<void>(`${ADDON_GROUPS_BASE}/${id}`, {
    method: "DELETE",
    authenticated: true,
  });

// ---- Service groups (Appliance/Service Group catalog redesign) ----

export const listServiceGroups = (categoryId?: string) =>
  apiFetch<ServiceGroupAdminResponse[]>(`${SERVICE_GROUPS_BASE}${query({ categoryId })}`, { authenticated: true });

export const getServiceGroup = (id: string) =>
  apiFetch<ServiceGroupAdminResponse>(`${SERVICE_GROUPS_BASE}/${id}`, { authenticated: true });

export const createServiceGroup = (request: ServiceGroupCreateRequest) =>
  apiFetch<ServiceGroupAdminResponse>(SERVICE_GROUPS_BASE, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updateServiceGroup = (id: string, request: ServiceGroupUpdateRequest) =>
  apiFetch<ServiceGroupAdminResponse>(`${SERVICE_GROUPS_BASE}/${id}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setServiceGroupActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${SERVICE_GROUPS_BASE}/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

export const deleteServiceGroup = (id: string) =>
  apiFetch<void>(`${SERVICE_GROUPS_BASE}/${id}`, {
    method: "DELETE",
    authenticated: true,
  });

// ---- Category groups (mirrors Service groups, one taxonomy level up) ----

export const listCategoryGroups = (categoryId?: string) =>
  apiFetch<CategoryGroupAdminResponse[]>(`${CATEGORY_GROUPS_BASE}${query({ categoryId })}`, { authenticated: true });

export const getCategoryGroup = (id: string) =>
  apiFetch<CategoryGroupAdminResponse>(`${CATEGORY_GROUPS_BASE}/${id}`, { authenticated: true });

export const createCategoryGroup = (request: CategoryGroupCreateRequest) =>
  apiFetch<CategoryGroupAdminResponse>(CATEGORY_GROUPS_BASE, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const updateCategoryGroup = (id: string, request: CategoryGroupUpdateRequest) =>
  apiFetch<CategoryGroupAdminResponse>(`${CATEGORY_GROUPS_BASE}/${id}`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const setCategoryGroupActive = (id: string, isActive: boolean) =>
  apiFetch<void>(`${CATEGORY_GROUPS_BASE}/${id}/${isActive ? "activate" : "deactivate"}`, {
    method: "POST",
    authenticated: true,
  });

export const deleteCategoryGroup = (id: string) =>
  apiFetch<void>(`${CATEGORY_GROUPS_BASE}/${id}`, {
    method: "DELETE",
    authenticated: true,
  });
