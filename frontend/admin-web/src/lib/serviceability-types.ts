/**
 * Response/request shapes for the Admin API's serviceability surface
 * (SRS 12.9, task 111/112): `GeographyController` (geography master - state,
 * city, zone, locality, pincode) and `ServiceabilityMappingsController`
 * (category/city and service/pincode serviceability mapping). Mirrors the
 * backend contracts in `Application/Geography/GeographyManagementContracts.cs`
 * and `Application/Serviceability/ServiceabilityMappingContracts.cs` field for
 * field.
 */

export interface StateResponse {
  id: string;
  name: string;
  code: string;
  isActive: boolean;
}

export interface StateCreateRequest {
  name: string;
  code: string;
}

export interface StateUpdateRequest {
  name: string;
}

export interface CityAdminResponse {
  id: string;
  stateId: string;
  stateName: string;
  name: string;
  isActive: boolean;
}

export interface CityCreateRequest {
  stateId: string;
  name: string;
}

export interface CityUpdateRequest {
  name: string;
}

export interface ZoneResponse {
  id: string;
  cityId: string;
  cityName: string;
  name: string;
  isActive: boolean;
}

export interface ZoneCreateRequest {
  cityId: string;
  name: string;
}

export interface ZoneUpdateRequest {
  name: string;
}

export interface LocalityAdminResponse {
  id: string;
  zoneId: string;
  zoneName: string;
  pincodeId: string;
  pincodeCode: string;
  name: string;
  isActive: boolean;
}

export interface LocalityCreateRequest {
  zoneId: string;
  pincodeId: string;
  name: string;
}

export interface LocalityUpdateRequest {
  name: string;
}

export interface PincodeAdminResponse {
  id: string;
  cityId: string;
  cityName: string;
  code: string;
  isActive: boolean;
}

export interface PincodeCreateRequest {
  cityId: string;
  code: string;
}

export interface CategoryLookupResponse {
  id: string;
  name: string;
}

export interface ServiceLookupResponse {
  id: string;
  name: string;
}

export interface CategoryCityMappingResponse {
  id: string;
  categoryId: string;
  categoryName: string;
  cityId: string;
  cityName: string;
  isActive: boolean;
}

export interface CategoryCityMappingCreateRequest {
  categoryId: string;
  cityId: string;
}

export interface ServicePincodeMappingResponse {
  id: string;
  serviceId: string;
  serviceName: string;
  pincodeId: string;
  pincodeCode: string;
  isActive: boolean;
}

export interface ServicePincodeMappingCreateRequest {
  serviceId: string;
  pincodeId: string;
}

/** An active service with zero active pincode mappings anywhere — launched but unbookable everywhere. */
export interface UnmappedActiveServiceResponse {
  serviceId: string;
  serviceName: string;
  serviceSlug: string;
  categoryId: string;
  categoryName: string;
}

/**
 * A (service, pincode) pair where an active provider already has matching
 * skill + area coverage but no active serviceability mapping exists for it.
 */
export interface ServiceabilityCoverageGapResponse {
  serviceId: string;
  serviceName: string;
  pincodeId: string;
  pincodeCode: string;
}

/**
 * An active service/pincode mapping with no active provider able to fulfil
 * it — the mapping is correct, but nobody is actually onboarded to serve it.
 */
export interface MappedPincodeWithoutProviderCoverageResponse {
  mappingId: string;
  serviceId: string;
  serviceName: string;
  pincodeId: string;
  pincodeCode: string;
}
