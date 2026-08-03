/**
 * Read-only catalog/geography lookup entries (task 205) - name lookups for
 * the skills and service-areas pickers, replacing the previous hand-typed
 * GUID inputs. Deliberately minimal (id/name only), mirroring the backend's
 * `ProviderCatalogCategoryResponse`/`ProviderGeographyCityResponse` etc. shapes.
 */
export interface CatalogCategory {
  id: string;
  name: string;
}

export interface CatalogService {
  id: string;
  categoryId: string;
  name: string;
}

export interface GeographyCity {
  id: string;
  name: string;
}

export interface GeographyZone {
  id: string;
  cityId: string;
  name: string;
}

export interface GeographyPincode {
  id: string;
  cityId: string;
  code: string;
}
