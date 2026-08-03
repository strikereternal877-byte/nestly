/**
 * Typed client for the Provider API's availability surface
 * (`/api/v1/availability`). Every call is authenticated.
 */
import { API_V1, apiFetch } from "./api";
import type {
  AvailabilityResponse,
  AvailabilityWindow,
  BlackoutDate,
  CreateBlackoutDateRequest,
  UpdateAvailabilityWindowsRequest,
} from "./availability-types";

const AVAILABILITY_BASE = `${API_V1}/availability`;

export const getAvailability = () =>
  apiFetch<AvailabilityResponse>(AVAILABILITY_BASE, { authenticated: true });

export const updateAvailabilityWindows = (request: UpdateAvailabilityWindowsRequest) =>
  apiFetch<AvailabilityWindow[]>(`${AVAILABILITY_BASE}/windows`, {
    method: "PUT",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const listBlackoutDates = () =>
  apiFetch<BlackoutDate[]>(`${AVAILABILITY_BASE}/blackout-dates`, { authenticated: true });

export const createBlackoutDate = (request: CreateBlackoutDateRequest) =>
  apiFetch<BlackoutDate>(`${AVAILABILITY_BASE}/blackout-dates`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const deleteBlackoutDate = (id: string) =>
  apiFetch<void>(`${AVAILABILITY_BASE}/blackout-dates/${id}`, {
    method: "DELETE",
    authenticated: true,
  });
