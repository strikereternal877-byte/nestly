import { useQuery } from "@tanstack/react-query";
import { API_V1, apiFetch } from "@/lib/api";

/**
 * Adding money to the wallet through the payment gateway (backend: WalletController /top-up*, /top-ups*).
 * Switched off by default on the server (`WalletTopUp:Enabled`) - it holds customers' money - so every screen
 * asks {@link useWalletTopUpConfig} first and offers nothing unless `enabled`.
 */

/** Mirrors Nestly.Domain.WalletTopUpStatus's declaration order (the API serialises enums as numbers). */
export enum WalletTopUpStatus {
  Pending = 0,
  Success = 1,
  Failed = 2,
}

export interface WalletTopUpConfig {
  enabled: boolean;
  minAmount: number;
  maxAmount: number;
  maxWalletBalance: number;
  suggestedAmounts: number[];
}

export interface WalletTopUpOrder {
  topUpId: string;
  gatewayOrderId: string;
  amount: number;
  currency: string;
  /** Set for a hosted-checkout gateway (PayU): POST `checkoutFormFields` there. Null for the sandbox. */
  checkoutRedirectUrl: string | null;
  checkoutFormFields: Record<string, string> | null;
}

export interface WalletTopUp {
  id: string;
  amount: number;
  currency: string;
  status: WalletTopUpStatus;
  failureReason: string | null;
  createdAtUtc: string;
  completedAtUtc: string | null;
  /** The wallet balance now, so a result screen needs no second request. */
  walletBalance: number;
}

/** Whether top-ups are on, and their limits. Cached for the session - it only changes with a deployment. */
export function useWalletTopUpConfig(enabled = true) {
  return useQuery({
    queryKey: ["wallet-topup-config"] as const,
    queryFn: () =>
      apiFetch<WalletTopUpConfig>(`${API_V1}/wallet/top-up/config`, { authenticated: true }),
    enabled,
    staleTime: 5 * 60_000,
    retry: false,
  });
}

export function createWalletTopUp(amount: number): Promise<WalletTopUpOrder> {
  return apiFetch<WalletTopUpOrder>(`${API_V1}/wallet/top-ups`, {
    method: "POST",
    authenticated: true,
    body: JSON.stringify({ amount }),
  });
}

export function getWalletTopUp(id: string): Promise<WalletTopUp> {
  return apiFetch<WalletTopUp>(`${API_V1}/wallet/top-ups/${id}`, { authenticated: true });
}

/** Asks the gateway directly how a pending top-up ended - for a checkout that was abandoned or whose webhook never came. */
export function verifyWalletTopUp(id: string): Promise<WalletTopUp> {
  return apiFetch<WalletTopUp>(`${API_V1}/wallet/top-ups/${id}/verify`, {
    method: "POST",
    authenticated: true,
  });
}

/** Sandbox only: completes the top-up the way the gateway's callback would. The server refuses it against a real gateway. */
export function simulateWalletTopUp(id: string): Promise<WalletTopUp> {
  return apiFetch<WalletTopUp>(`${API_V1}/wallet/top-ups/${id}/simulate`, {
    method: "POST",
    authenticated: true,
  });
}

/** Where to send the customer back to after a top-up that started from another screen (a booking summary). */
const RETURN_TO_KEY = "nestly.wallet-topup.return-to";

export function rememberTopUpReturnPath(path: string | null): void {
  try {
    if (path) sessionStorage.setItem(RETURN_TO_KEY, path);
    else sessionStorage.removeItem(RETURN_TO_KEY);
  } catch {
    // Private mode / blocked storage: the customer just lands on the wallet instead of back on the booking.
  }
}

export function takeTopUpReturnPath(): string | null {
  try {
    const value = sessionStorage.getItem(RETURN_TO_KEY);
    return value;
  } catch {
    return null;
  }
}
