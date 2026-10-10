/**
 * Typed client for the Admin API's provider ticket workflow surface
 * (Provider Management UX pass): `ProviderSupportTicketsController` -
 * search/detail, reply, resolve. Every call is authenticated, gated behind
 * the "support" permission module server-side (same as the customer ticket
 * workflow's own SupportTicketsController).
 */
import { API_V1, apiFetch } from "./api";
import type {
  AddProviderSupportTicketCommentRequestBody,
  AdminProviderSupportTicketDetailResponse,
  AdminProviderSupportTicketSearchResponse,
  ProviderSupportTicketSearchParams,
  ResolveProviderSupportTicketRequestBody,
} from "./provider-support-types";

const PROVIDER_SUPPORT_TICKETS_BASE = `${API_V1}/provider-support-tickets`;

function query(params: ProviderSupportTicketSearchParams): string {
  const entries = Object.entries(params as Record<string, string | number | undefined>)
    .filter(([, value]) => value !== undefined);
  if (entries.length === 0) return "";
  return `?${new URLSearchParams(entries.map(([key, value]) => [key, String(value)])).toString()}`;
}

export const searchProviderSupportTickets = (params: ProviderSupportTicketSearchParams = {}) =>
  apiFetch<AdminProviderSupportTicketSearchResponse>(`${PROVIDER_SUPPORT_TICKETS_BASE}${query(params)}`, {
    authenticated: true,
  });

export const getProviderSupportTicket = (id: string) =>
  apiFetch<AdminProviderSupportTicketDetailResponse>(`${PROVIDER_SUPPORT_TICKETS_BASE}/${id}`, { authenticated: true });

export const replyToProviderSupportTicket = (id: string, body: AddProviderSupportTicketCommentRequestBody) =>
  apiFetch<AdminProviderSupportTicketDetailResponse>(`${PROVIDER_SUPPORT_TICKETS_BASE}/${id}/reply`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(body),
  });

export const resolveProviderSupportTicket = (id: string, body: ResolveProviderSupportTicketRequestBody) =>
  apiFetch<AdminProviderSupportTicketDetailResponse>(`${PROVIDER_SUPPORT_TICKETS_BASE}/${id}/resolve`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(body),
  });
