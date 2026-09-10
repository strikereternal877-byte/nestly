/**
 * Types for the Admin API's catalog management surface (SRS 12.5-12.7, tasks
 * 103a-107): categories, services/packages and add-ons. Mirrors the backend
 * contracts in backend/shared/Application/Catalog/*ManagementContracts.cs
 * field-for-field.
 */

// ---- Categories (SRS 12.5) ----

export interface CategoryResponse {
  id: string;
  name: string;
  slug: string;
  description: string;
  iconUrl: string | null;
  bannerUrl: string | null;
  pageBannerUrl: string | null;
  isActive: boolean;
  isFeatured: boolean;
  sortOrder: number;
  seoTitle: string | null;
  seoMetaDescription: string | null;
  /** Null for a top-level category (Phase 3 catalog redesign). */
  parentCategoryId: string | null;
  /** Null when this category renders directly under its parent's subcategory listing with no section header (the default). */
  categoryGroupId: string | null;
}

export interface CategoryCreateRequest {
  name: string;
  slug: string;
  description: string;
  iconUrl: string | null;
  bannerUrl: string | null;
  pageBannerUrl: string | null;
  sortOrder: number;
  seoTitle: string | null;
  seoMetaDescription: string | null;
  parentCategoryId: string | null;
  categoryGroupId: string | null;
}

export type CategoryUpdateRequest = CategoryCreateRequest;

// ---- Services / packages (SRS 12.6) ----

export type ServicePricingType = "Fixed" | "Variable";

export interface ServiceAdminResponse {
  id: string;
  categoryId: string;
  categoryName: string;
  name: string;
  slug: string;
  description: string;
  shortDescription: string | null;
  price: number;
  isActive: boolean;
  /** Photo shown on customer-facing listing cards. Null renders a graphic fallback there. */
  coverImageUrl: string | null;
  inclusions: string;
  exclusions: string;
  cancellationPolicy: string | null;
  reschedulePolicy: string | null;
  durationMinutes: number;
  isFeatured: boolean;
  sortOrder: number;
  seoTitle: string | null;
  seoMetaDescription: string | null;
  pricingType: ServicePricingType;
  isTaxApplicable: boolean;
  isAddOnAllowed: boolean;
  isQuantityAllowed: boolean;
  isInspectionBased: boolean;
  isSlotRequired: boolean;
  isAddressRequired: boolean;
  isCustomerNoteAllowed: boolean;
  /** Whether the service is sold as a block of time (provider stays for the booked duration) rather than fixed-scope (freed on completion). Governs early release of the provider for their next job. */
  isDurationBased: boolean;
  /** Null when the service renders directly under its category with no section header (the default). Appliance/Service Group catalog redesign. */
  serviceGroupId: string | null;
}

export interface ServiceCreateRequest {
  categoryId: string;
  name: string;
  slug: string;
  description: string;
  shortDescription: string | null;
  price: number;
  coverImageUrl: string | null;
  inclusions: string;
  exclusions: string;
  cancellationPolicy: string | null;
  reschedulePolicy: string | null;
  durationMinutes: number;
  sortOrder: number;
  seoTitle: string | null;
  seoMetaDescription: string | null;
  pricingType: ServicePricingType;
  isTaxApplicable: boolean;
  isAddOnAllowed: boolean;
  isQuantityAllowed: boolean;
  isInspectionBased: boolean;
  isSlotRequired: boolean;
  isAddressRequired: boolean;
  isCustomerNoteAllowed: boolean;
  isDurationBased: boolean;
  serviceGroupId: string | null;
}

export type ServiceUpdateRequest = ServiceCreateRequest;

export interface ServiceMediaResponse {
  id: string;
  serviceId: string;
  url: string;
}

export interface ServiceMediaCreateRequest {
  url: string;
}

// ---- Catalog health (docs/OPEN-FIXES-FEATURES.csv "Admin Web, Proposed new page, Catalog health") ----

/** Reason codes CatalogHealthIssueResponse.reasons can contain — stable strings matched against CATALOG_HEALTH_REASON_LABELS to render badges. */
export type CatalogHealthReason = "NoPrice" | "NoImage" | "NoMapping" | "NeverBooked";

/**
 * An active service failing at least one pre-publish completeness check.
 * Warning/audit only — never a block on creating or activating a service.
 * Only services with at least one failing check are returned by the backend.
 */
export interface CatalogHealthIssueResponse {
  serviceId: string;
  serviceName: string;
  serviceSlug: string;
  categoryId: string;
  categoryName: string;
  reasons: CatalogHealthReason[];
}

// ---- Service variants (SRS 12.6 extension, Phase 3 catalog redesign) ----

export interface ServiceVariantAdminResponse {
  id: string;
  serviceId: string;
  name: string;
  price: number;
  durationMinutes: number;
  inclusionsOverride: string | null;
  isActive: boolean;
  sortOrder: number;
}

export interface ServiceVariantCreateRequest {
  name: string;
  price: number;
  durationMinutes: number;
  inclusionsOverride: string | null;
  sortOrder: number;
}

export type ServiceVariantUpdateRequest = ServiceVariantCreateRequest;

// ---- Add-ons (SRS 12.7) ----

export interface ServiceAddOnAdminResponse {
  id: string;
  serviceId: string;
  serviceName: string;
  name: string;
  description: string | null;
  price: number;
  isActive: boolean;
  sortOrder: number;
  isQuantityAllowed: boolean;
  isMandatory: boolean;
  /** Null when ungrouped (today's default). Phase 3 catalog redesign. */
  groupId: string | null;
}

export interface ServiceAddOnCreateRequest {
  serviceId: string;
  name: string;
  description: string | null;
  price: number;
  sortOrder: number;
  isQuantityAllowed: boolean;
  isMandatory: boolean;
  groupId: string | null;
}

export type ServiceAddOnUpdateRequest = ServiceAddOnCreateRequest;

// ---- Add-on groups (Phase 3 catalog redesign) ----

export type AddOnGroupSelectionType = "Single" | "Multiple";

export interface ServiceAddOnGroupAdminResponse {
  id: string;
  serviceId: string;
  serviceName: string;
  name: string;
  selectionType: AddOnGroupSelectionType;
  minSelect: number;
  maxSelect: number | null;
  sortOrder: number;
}

export interface ServiceAddOnGroupCreateRequest {
  serviceId: string;
  name: string;
  selectionType: AddOnGroupSelectionType;
  minSelect: number;
  maxSelect: number | null;
  sortOrder: number;
}

export type ServiceAddOnGroupUpdateRequest = ServiceAddOnGroupCreateRequest;

// ---- Service groups (Appliance/Service Group catalog redesign) ----
//
// An optional section header for a subset of a category's services (e.g.
// "Repair & gas refill" under "AC"). Distinct from an add-on group above:
// this groups a category's bookable services, not one service's add-ons.

export interface ServiceGroupAdminResponse {
  id: string;
  categoryId: string;
  categoryName: string;
  name: string;
  isActive: boolean;
  sortOrder: number;
}

export interface ServiceGroupCreateRequest {
  categoryId: string;
  name: string;
  sortOrder: number;
}

export type ServiceGroupUpdateRequest = ServiceGroupCreateRequest;

// ---- Category groups (mirrors Service groups, one taxonomy level up) ----
//
// An optional section header for a subset of a PARENT category's
// subcategories (e.g. "Large appliances" under "AC & Appliance Repair").
// `categoryId` here names the parent whose subcategory listing this group
// organizes, not a subcategory itself.

export interface CategoryGroupAdminResponse {
  id: string;
  categoryId: string;
  categoryName: string;
  name: string;
  isActive: boolean;
  sortOrder: number;
}

export interface CategoryGroupCreateRequest {
  categoryId: string;
  name: string;
  sortOrder: number;
}

export type CategoryGroupUpdateRequest = CategoryGroupCreateRequest;
