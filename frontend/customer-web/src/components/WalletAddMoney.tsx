"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { inr } from "@/components/patterns";
import { Alert, Button, Card, Field, cx } from "@/components/ui";
import { describeError } from "@/lib/api";
import { submitToPayU } from "@/lib/payu-checkout";
import {
  WalletTopUpStatus,
  createWalletTopUp,
  rememberTopUpReturnPath,
  simulateWalletTopUp,
} from "@/lib/wallet-topup";
import type { WalletTopUp, WalletTopUpConfig, WalletTopUpOrder } from "@/lib/wallet-topup";

/**
 * "Add money" on the wallet screen: the customer picks an amount and pays it through the gateway (PayU Hosted
 * Checkout - UPI, cards, netbanking), and the wallet is credited when the gateway confirms.
 *
 * Rendered only when the server says top-ups are on (see {@link useWalletTopUpConfig}); with them off the wallet
 * page shows nothing of this. Against the sandbox gateway there is no checkout page to go to, so the customer is
 * given a "complete payment" button instead - the same split the booking payment page makes.
 *
 * `returnTo` is where the customer came from (a booking summary): it is remembered across the trip to the
 * gateway so the result screen can offer "back to your booking".
 */
export function WalletAddMoney({
  config,
  returnTo,
  autoFocus,
}: {
  config: WalletTopUpConfig;
  returnTo: string | null;
  autoFocus?: boolean;
}) {
  const queryClient = useQueryClient();
  const anchor = useRef<HTMLDivElement>(null);
  const [amount, setAmount] = useState(String(config.suggestedAmounts[0] ?? config.minAmount));
  const [error, setError] = useState<string | null>(null);
  const [isStarting, setIsStarting] = useState(false);
  const [sandboxOrder, setSandboxOrder] = useState<WalletTopUpOrder | null>(null);
  const [isCompleting, setIsCompleting] = useState(false);
  const [outcome, setOutcome] = useState<WalletTopUp | null>(null);

  useEffect(() => {
    if (autoFocus) anchor.current?.scrollIntoView({ behavior: "smooth", block: "start" });
  }, [autoFocus]);

  const value = Number(amount);
  const hasAtMostTwoDecimals = Number.isFinite(value) && Math.round(value * 100) === value * 100;
  const isValid = Number.isFinite(value) && value >= config.minAmount && value <= config.maxAmount && hasAtMostTwoDecimals;

  const refreshWallet = () => {
    queryClient.invalidateQueries({ queryKey: ["wallet-balance"] });
    queryClient.invalidateQueries({ queryKey: ["wallet-ledger"] });
  };

  const handleStart = async () => {
    if (!isValid || isStarting) return;
    setError(null);
    setOutcome(null);
    setIsStarting(true);

    try {
      const order = await createWalletTopUp(value);

      if (order.checkoutRedirectUrl && order.checkoutFormFields) {
        // A real gateway: leave this app for its checkout. Remember where the customer came from first.
        rememberTopUpReturnPath(returnTo);
        submitToPayU(order.checkoutRedirectUrl, order.checkoutFormFields);
        return; // the browser is navigating away; the button stays busy
      }

      // Sandbox: nothing to redirect to - the customer completes it here.
      setSandboxOrder(order);
    } catch (err) {
      setError(describeError(err));
    }

    setIsStarting(false);
  };

  const handleCompleteSandbox = async () => {
    if (!sandboxOrder || isCompleting) return;
    setError(null);
    setIsCompleting(true);

    try {
      const result = await simulateWalletTopUp(sandboxOrder.topUpId);
      setOutcome(result);
      setSandboxOrder(null);
      refreshWallet();
    } catch (err) {
      setError(describeError(err));
    } finally {
      setIsCompleting(false);
    }
  };

  return (
    <div id="add-money" ref={anchor}>
      <Card
        title="Add money"
        description="Add money from your bank, UPI or card. It's yours to spend on any Glavyx service."
      >
        <div className="flex flex-col gap-4">
          {config.suggestedAmounts.length > 0 ? (
            <div role="group" aria-label="Quick amounts" className="flex flex-wrap gap-2">
              {config.suggestedAmounts.map((suggested) => {
                const isSelected = value === suggested;
                return (
                  <button
                    key={suggested}
                    type="button"
                    aria-pressed={isSelected}
                    onClick={() => setAmount(String(suggested))}
                    className={cx(
                      "nums rounded-xl border px-3.5 py-2 text-sm font-medium transition duration-fast ease-out",
                      isSelected
                        ? "border-brand-600 bg-brand-600 text-fg-on-brand shadow-brand"
                        : "border-line bg-surface text-fg hover:border-line-strong hover:bg-surface-2",
                    )}
                  >
                    {inr(suggested)}
                  </button>
                );
              })}
            </div>
          ) : null}

          <Field
            id="wallet-add-amount"
            label="Amount (₹)"
            type="number"
            inputMode="decimal"
            min={config.minAmount}
            max={config.maxAmount}
            className="max-w-[12rem]"
            value={amount}
            onChange={(e) => setAmount(e.target.value)}
            hint={`${inr(config.minAmount)} to ${inr(config.maxAmount)} at a time. Your wallet can hold up to ${inr(config.maxWalletBalance)}.`}
            error={amount !== "" && !isValid ? `Enter an amount between ${inr(config.minAmount)} and ${inr(config.maxAmount)}.` : undefined}
          />

          <p className="text-xs leading-relaxed text-fg-subtle">
            Money you add can only be used on Glavyx services - it can&apos;t be withdrawn to your bank.
          </p>

          {error ? (
            <Alert tone="error" title="Couldn't add money">
              {error}
            </Alert>
          ) : null}

          {outcome ? (
            outcome.status === WalletTopUpStatus.Success ? (
              <Alert tone="success" title={`${inr(outcome.amount)} added to your wallet`}>
                Your balance is now {inr(outcome.walletBalance)}.
              </Alert>
            ) : (
              <Alert tone="error" title="The payment didn't go through">
                {outcome.failureReason ?? "No money was added. You can try again."}
              </Alert>
            )
          ) : null}

          {sandboxOrder ? (
            <div className="flex flex-col gap-3 rounded-xl border border-line bg-surface-2 p-4">
              <p className="text-sm leading-relaxed text-fg-muted">
                Sandbox: there is no real payment page here. Complete the {inr(sandboxOrder.amount)} top-up below
                to simulate the gateway confirming it.
              </p>
              <Button type="button" loading={isCompleting} onClick={handleCompleteSandbox}>
                {`Complete ${inr(sandboxOrder.amount)} (Sandbox)`}
              </Button>
            </div>
          ) : (
            <Button type="button" loading={isStarting} disabled={!isValid} onClick={handleStart}>
              {isValid ? `Add ${inr(value)}` : "Add money"}
            </Button>
          )}
        </div>
      </Card>
    </div>
  );
}
