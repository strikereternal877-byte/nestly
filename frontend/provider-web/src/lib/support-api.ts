/**
 * Typed client for the Provider API's support ticket surface
 * (`/api/v1/support-tickets`). Every call is authenticated.
 */
import { API_V1, apiFetch } from "./api";
import type {
  AddProviderSupportTicketCommentRequest,
  CreateProviderSupportTicketRequest,
  ProviderSupportTicketDetail,
  ProviderSupportTicketSummary,
} from "./support-types";

const SUPPORT_TICKETS_BASE = `${API_V1}/support-tickets`;

export const createSupportTicket = (request: CreateProviderSupportTicketRequest) =>
  apiFetch<ProviderSupportTicketDetail>(SUPPORT_TICKETS_BASE, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });

export const listSupportTickets = () =>
  apiFetch<ProviderSupportTicketSummary[]>(SUPPORT_TICKETS_BASE, { authenticated: true });

export const getSupportTicketDetail = (ticketId: string) =>
  apiFetch<ProviderSupportTicketDetail>(`${SUPPORT_TICKETS_BASE}/${ticketId}`, { authenticated: true });

export const addSupportTicketComment = (ticketId: string, request: AddProviderSupportTicketCommentRequest) =>
  apiFetch<ProviderSupportTicketDetail>(`${SUPPORT_TICKETS_BASE}/${ticketId}/comments`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify(request),
  });
