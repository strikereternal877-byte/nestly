/**
 * Typed client for the Provider API's notification inbox
 * (`/api/v1/notifications`). Every call is authenticated.
 */
import { API_V1, apiFetch } from "./api";
import type { ProviderNotification, ProviderNotificationListResponse } from "./notifications-types";

const NOTIFICATIONS_BASE = `${API_V1}/notifications`;

export const listNotifications = (page = 1, pageSize = 20) =>
  apiFetch<ProviderNotificationListResponse>(`${NOTIFICATIONS_BASE}?page=${page}&pageSize=${pageSize}`, {
    authenticated: true,
  });

export const markNotificationRead = (notificationId: string) =>
  apiFetch<ProviderNotification>(`${NOTIFICATIONS_BASE}/${notificationId}/read`, {
    method: "POST",
    authenticated: true,
  });

export const markAllNotificationsRead = () =>
  apiFetch<void>(`${NOTIFICATIONS_BASE}/read-all`, { method: "POST", authenticated: true });
