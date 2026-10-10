"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useParams } from "next/navigation";
import { useEffect, useState } from "react";
import { BannerBreadcrumb, ScreenSkeleton, inr } from "@/components/patterns";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import { Alert, Button, Card, LinkButton, Spinner } from "@/components/ui";
import { describeError } from "@/lib/api";
import { isSafeReturnPath } from "@/lib/return-to";
import {
  WalletTopUpStatus,
  getWalletTopUp,
  rememberTopUpReturnPath,
  takeTopUpReturnPath,
  verifyWalletTopUp,
} from "@/lib/wallet-topup";

const POLL_INTERVAL_MS = 2000;
const POLL_TIMEOUT_MS = 20000;

/**
 * Where PayU sends the browser back to after a wallet top-up's Hosted Checkout (both its success and failure
 * redirect point here). As on the booking payment return page, the real outcome is whatever our own webhook
 * recorded, not which URL PayU picked, so this page only re-checks that and says what happened: it polls while the
 * top-up is pending, and once the webhook has had its head start it asks the gateway directly (an abandoned
 * checkout may never trigger a webhook at all).
 */
export default function WalletTopUpReturnPage() {
  return (
    <RequireAuth>
      <WalletTopUpReturnScreen />
    </RequireAuth>
  );
}

function WalletTopUpReturnScreen() {
  const queryClient = useQueryClient();
  const { id } = useParams<{ id: string }>();
  const [timedOut, setTimedOut] = useState(false);
  // Where the customer started (a booking summary), remembered across the trip to the gateway. The value sat in
  // session storage, which a hand-edited tab could have changed, so it is re-checked before it becomes a link.
  const [returnTo] = useState<string | null>(() => {
    const stored = takeTopUpReturnPath();
    return isSafeReturnPath(stored) ? stored : null;
  });

  useEffect(() => {
    const timer = setTimeout(() => setTimedOut(true), POLL_TIMEOUT_MS);
    return () => clearTimeout(timer);
  }, []);

  const topUpQuery = useQuery({
    queryKey: ["wallet-topup", id],
    queryFn: () => getWalletTopUp(id),
    refetchInterval: (query) => {
      if (query.state.data?.status !== WalletTopUpStatus.Pending) return false;
      return timedOut ? false : POLL_INTERVAL_MS;
    },
  });

  const topUp = topUpQuery.data;
  const isSuccess = topUp?.status === WalletTopUpStatus.Success;
  const isFailed = topUp?.status === WalletTopUpStatus.Failed;
  const stillPending = topUp?.status === WalletTopUpStatus.Pending && timedOut;

  const verifyMutation = useMutation({
    mutationFn: () => verifyWalletTopUp(id),
    onSettled: () => topUpQuery.refetch(),
  });

  useEffect(() => {
    if (stillPending) verifyMutation.mutate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stillPending]);

  useEffect(() => {
    if (!isSuccess) return;
    queryClient.invalidateQueries({ queryKey: ["wallet-balance"] });
    queryClient.invalidateQueries({ queryKey: ["wallet-ledger"] });
  }, [isSuccess, queryClient]);

  // The remembered destination has done its job once the top-up has ended either way.
  useEffect(() => {
    if (isSuccess || isFailed) rememberTopUpReturnPath(null);
  }, [isSuccess, isFailed]);

  if (topUpQuery.isPending) {
    return (
      <main className="flex w-full flex-col">
        <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
        <ScreenSkeleton cards={1} className="mx-auto w-full max-w-3xl px-4 py-10 sm:px-6 sm:py-14" />
      </main>
    );
  }

  if (topUpQuery.isError || !topUp) {
    return (
      <main className="mx-auto w-full max-w-3xl px-4 py-8 sm:px-6 sm:py-12">
        <Alert
          tone="error"
          title="Couldn't check your payment"
          action={
            <Button size="sm" variant="secondary" onClick={() => topUpQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(topUpQuery.error)}
        </Alert>
      </main>
    );
  }

  const tryAgainHref = returnTo
    ? `/wallet?addMoney=1&returnTo=${encodeURIComponent(returnTo)}`
    : "/wallet?addMoney=1";

  return (
    <main className="flex w-full animate-rise flex-col">
      <PageBanner
        title="Add money"
        description="Payment status"
        breadcrumb={
          <BannerBreadcrumb
            items={[{ label: "Home", href: "/" }, { label: "Wallet", href: "/wallet" }, { label: "Payment status" }]}
          />
        }
      />

      <div className="mx-auto flex w-full max-w-3xl flex-col gap-6 px-4 py-10 sm:px-6 sm:py-14">
        {isSuccess ? (
          <Card title="Money added">
            <div className="flex flex-col gap-3">
              <Alert tone="success" title={`${inr(topUp.amount)} added to your wallet`}>
                Your balance is now {inr(topUp.walletBalance)}.
              </Alert>
              <div className="flex flex-col gap-2 sm:flex-row">
                {returnTo ? (
                  <LinkButton href={returnTo} fullWidth>
                    Back to your booking
                  </LinkButton>
                ) : null}
                <LinkButton href="/wallet" variant={returnTo ? "secondary" : "primary"} fullWidth>
                  View wallet
                </LinkButton>
              </div>
            </div>
          </Card>
        ) : isFailed ? (
          <Card title="Payment failed">
            <div className="flex flex-col gap-3">
              <Alert tone="error" title="Your payment didn't go through">
                {topUp.failureReason ?? "No money was added to your wallet. You can try again right away."}
              </Alert>
              <div className="flex flex-col gap-2 sm:flex-row">
                <LinkButton href={tryAgainHref} fullWidth>
                  Try again
                </LinkButton>
                {returnTo ? (
                  <LinkButton href={returnTo} variant="ghost" fullWidth>
                    Back to your booking
                  </LinkButton>
                ) : null}
              </div>
            </div>
          </Card>
        ) : stillPending ? (
          <Card title="Still confirming your payment">
            <div className="flex flex-col gap-3">
              <Alert tone="warning" title="This is taking longer than usual">
                We haven&apos;t received confirmation from the payment gateway yet. If money was deducted, it will
                appear in your wallet automatically within a few minutes - you don&apos;t need to pay again.
              </Alert>
              <div className="flex flex-col gap-2 sm:flex-row">
                <Button size="sm" variant="secondary" onClick={() => topUpQuery.refetch()}>
                  Check again
                </Button>
                <LinkButton href="/wallet" size="sm" variant="ghost">
                  View wallet
                </LinkButton>
              </div>
            </div>
          </Card>
        ) : (
          <Card title="Confirming your payment">
            <div className="flex items-center gap-3 text-sm text-fg-muted">
              <Spinner />
              Please wait while we confirm your payment…
            </div>
          </Card>
        )}
      </div>
    </main>
  );
}
