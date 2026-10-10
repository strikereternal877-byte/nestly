"use client";

import { useQuery } from "@tanstack/react-query";
import type { UseQueryResult } from "@tanstack/react-query";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { BannerBreadcrumb, ScreenSkeleton, formatInstant, inr } from "@/components/patterns";
import { PageBanner } from "@/components/PageBanner";
import { RequireAuth } from "@/components/RequireAuth";
import { WalletAddMoney } from "@/components/WalletAddMoney";
import {
  Alert,
  Badge,
  Button,
  Card,
  EmptyState,
  LinkButton,
  Skeleton,
  TBody,
  TD,
  TH,
  THead,
  TR,
  Table,
  cx,
} from "@/components/ui";
import type { BadgeTone } from "@/components/ui";
import { API_V1, apiFetch, describeError } from "@/lib/api";
import { isSafeReturnPath } from "@/lib/return-to";
import { WalletEntryType, WalletSourceType } from "@/lib/types";
import type { WalletBalanceResponse, WalletLedgerEntryResponse } from "@/lib/types";
import { useWalletTopUpConfig } from "@/lib/wallet-topup";

/**
 * Wallet balance and ledger (tasks 78a-b, SRS 11.17), and - when the server has it switched on - adding money.
 *
 * Wrapped in Suspense for useSearchParams (see booking/summary/page.tsx for why).
 */
export default function WalletPage() {
  return (
    <Suspense
      fallback={
        <main className="flex w-full flex-col">
          <div className="listing-banner h-[13.5rem] w-full sm:h-[15.5rem]" aria-hidden />
          <ScreenSkeleton cards={2} className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14" />
        </main>
      }
    >
      <RequireAuth>
        <WalletScreen />
      </RequireAuth>
    </Suspense>
  );
}

/**
 * Readable label for a ledger entry's source (SRS 11.17, GUIDELINES #4 of
 * docs/NESTLY-COINS.md - "a breakdown showing how much of the applied
 * balance was coins vs. other credit"). Real gap fixed here, not just
 * NestlyCoinsReward added alongside it (task 203): the three Referral
 * source types already existed on the backend but were never added to this
 * switch, silently falling through to the generic "Wallet" label.
 */
function sourceLabel(sourceType: WalletSourceType): string {
  switch (sourceType) {
    case WalletSourceType.Refund:
      return "Refund";
    case WalletSourceType.PromotionalCredit:
      return "Promotional credit";
    case WalletSourceType.ManualAdjustment:
      return "Adjustment";
    case WalletSourceType.ReferralReward:
      return "Referral reward";
    case WalletSourceType.ReferralMilestoneBonus:
      return "Referral milestone bonus";
    case WalletSourceType.ReferralCreditExpiry:
      return "Referral credit expired";
    case WalletSourceType.NestlyCoinsReward:
      return "Glavyx Coins earned";
    case WalletSourceType.NestlyCoinsClawback:
      return "Glavyx Coins clawed back";
    case WalletSourceType.BookingWalletCredit:
      return "Applied to booking";
    case WalletSourceType.BookingWalletCreditReversal:
      return "Wallet credit reversed - booking refunded";
    case WalletSourceType.TopUp:
      return "Money added";
    case WalletSourceType.RescheduleFee:
      return "Late reschedule fee";
    case WalletSourceType.RescheduleFeeReversal:
      return "Late reschedule fee returned";
    default:
      return "Wallet";
  }
}

/**
 * `accent` is the reward tone across the product, so the two Nestly Coins
 * sources carry it and nothing else does — that is the whole point of keeping
 * the ramp reserved. Everything else takes a neutral pill: the credit/debit
 * direction is already carried by the amount column, and colouring the source
 * as well would say the same thing twice in two different vocabularies.
 */
function sourceTone(sourceType: WalletSourceType): BadgeTone {
  switch (sourceType) {
    case WalletSourceType.NestlyCoinsReward:
    case WalletSourceType.NestlyCoinsClawback:
      return "accent";
    case WalletSourceType.ReferralReward:
    case WalletSourceType.ReferralMilestoneBonus:
      return "brand";
    default:
      return "neutral";
  }
}

function WalletScreen() {
  const params = useSearchParams();
  // Set when the customer came here from a booking summary to top up: the add-money panel is brought into view,
  // and the summary is remembered so the result screen can send them back to it.
  const rawReturnTo = params.get("returnTo");
  const returnTo = isSafeReturnPath(rawReturnTo) ? rawReturnTo : null;
  const wantsAddMoney = params.get("addMoney") === "1";

  const topUpConfigQuery = useWalletTopUpConfig();
  const topUpConfig = topUpConfigQuery.data?.enabled ? topUpConfigQuery.data : null;

  const balanceQuery = useQuery({
    queryKey: ["wallet-balance"],
    queryFn: () =>
      apiFetch<WalletBalanceResponse>(`${API_V1}/wallet/balance`, { authenticated: true }),
  });

  const ledgerQuery = useQuery({
    queryKey: ["wallet-ledger"],
    queryFn: () =>
      apiFetch<WalletLedgerEntryResponse[]>(`${API_V1}/wallet/ledger`, { authenticated: true }),
  });

  return (
    <main className="flex w-full flex-col">
      <PageBanner
        title="Wallet"
        description="Your Glavyx wallet balance and transaction history."
        breadcrumb={<BannerBreadcrumb items={[{ label: "Home", href: "/" }, { label: "Wallet" }]} />}
      />

      <div className="mx-auto w-full max-w-7xl px-4 py-10 sm:px-6 sm:py-14">
        <div className="flex animate-rise flex-col gap-6">
          <BalanceCard query={balanceQuery} canAddMoney={topUpConfig !== null} />
          {topUpConfig ? (
            <WalletAddMoney config={topUpConfig} returnTo={returnTo} autoFocus={wantsAddMoney} />
          ) : null}
          <LedgerCard query={ledgerQuery} />
        </div>
      </div>
    </main>
  );
}

function BalanceCard({
  query,
  canAddMoney,
}: {
  query: UseQueryResult<WalletBalanceResponse>;
  canAddMoney: boolean;
}) {
  if (query.isPending) {
    return (
      <Card title="Balance">
        <div className="flex flex-col gap-2" aria-hidden>
          <Skeleton className="h-9 w-40" />
          <Skeleton className="h-3.5 w-64" />
        </div>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Balance">
        <Alert
          tone="error"
          title="Couldn't load your balance"
          action={
            <Button size="sm" variant="secondary" onClick={() => query.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(query.error)}
        </Alert>
      </Card>
    );
  }

  return (
    <Card title="Balance">
      {/* The figure is refetched in place, so it is announced rather than
          silently swapped under a customer who is reading it. */}
      <p className="nums text-display-sm font-semibold text-fg" aria-live="polite">
        {inr(query.data.balance)}
      </p>
      <p className="mt-1.5 text-sm leading-relaxed text-fg-muted">
        Use it at checkout: turn on &ldquo;Use my wallet balance&rdquo; on your booking summary to
        put it towards that booking, after any coupon or subscription discount.
        {canAddMoney ? " It can also pay a daily plan's visits automatically." : ""}
      </p>
    </Card>
  );
}

/** Shared by the card list and the table so the two can never describe themselves differently to a screen reader. */
const LEDGER_CAPTION = "Wallet transactions, newest first, with the running balance after each one.";

function LedgerCard({
  query,
}: {
  query: UseQueryResult<WalletLedgerEntryResponse[]>;
}) {
  if (query.isPending) {
    return (
      <Card title="Transaction history">
        <div className="flex flex-col gap-4" aria-hidden>
          {[0, 1, 2, 3].map((row) => (
            <div key={row} className="flex items-center justify-between gap-4">
              <div className="flex min-w-0 flex-1 flex-col gap-2">
                <Skeleton className="h-3.5 w-40" />
                <Skeleton className="h-3 w-28" />
              </div>
              <Skeleton className="h-3.5 w-20" />
            </div>
          ))}
        </div>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Transaction history">
        <Alert
          tone="error"
          title="Couldn't load your transactions"
          action={
            <Button size="sm" variant="secondary" onClick={() => query.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(query.error)}
        </Alert>
      </Card>
    );
  }

  if (query.data.length === 0) {
    return (
      <EmptyState
        icon={<WalletIcon />}
        title="No wallet activity yet"
        description="Refunds, promotional credit and referral rewards all land here. Turn on &ldquo;Use my wallet balance&rdquo; on your next booking summary to put it towards that booking."
        action={<LinkButton href="/categories">Browse services</LinkButton>}
      />
    );
  }

  return (
    <Card title="Transaction history" flush>
      {/*
       * Task 365. Three columns of financial data is the case
       * docs/FRONTEND.md RESPONSIVE DESIGN asks to collapse rather than let
       * scroll sideways: `Table` confines the overflow to itself so it can
       * never break the page, but on a 375px phone "Balance after" still ends
       * up off-screen behind a scroll gesture nothing signals. So each entry
       * renders twice - once as the card below, once as a table row - with
       * exactly one visible at a time. The duplication is CSS-only: same
       * data, same query, no second fetch.
       *
       * `md` (768px) rather than admin-web's `lg`: it is the breakpoint this
       * app's own mobile/desktop split already uses (SiteHeader's menu,
       * BottomTabBar), and customer-web is mobile-first without
       * qualification, so the card is the primary layout here and the table
       * is the enhancement - the reverse of the admin.
       */}
      {/* The caption rides on the list itself rather than as an sr-only first
          child: a hidden <li> would be announced as a list item and inflate
          "list, N items" by one, which the <caption> equivalent below does
          not do. */}
      <ul aria-label={LEDGER_CAPTION} className="divide-y divide-line md:hidden">
        {query.data.map((entry) => (
          <LedgerCardRow key={entry.id} entry={entry} />
        ))}
      </ul>

      <div className="hidden md:block">
        <Table>
          <caption className="sr-only">{LEDGER_CAPTION}</caption>
          <THead>
            <TR>
              <TH>Transaction</TH>
              <TH numeric>Amount</TH>
              <TH numeric>Balance after</TH>
            </TR>
          </THead>
          <TBody>
            {query.data.map((entry) => (
              <LedgerRow key={entry.id} entry={entry} />
            ))}
          </TBody>
        </Table>
      </div>
    </Card>
  );
}

/**
 * The signed amount, shared by both layouts so the two can never disagree
 * about direction, sign or colour.
 *
 * Direction was previously carried by green/red alone, which is invisible to
 * anyone who cannot separate the two. The sign is the primary signal, the
 * colour reinforces it, and the word itself is there for a screen reader.
 */
function LedgerAmount({ entry }: { entry: WalletLedgerEntryResponse }) {
  const isCredit = entry.entryType === WalletEntryType.Credit;

  return (
    <span className={cx("font-semibold", isCredit ? "text-success" : "text-danger")}>
      <span className="sr-only">{isCredit ? "Credit " : "Debit "}</span>
      {isCredit ? "+" : "−"}
      {inr(entry.amount)}
    </span>
  );
}

/**
 * The identity of an entry - what it was, and when. Identical in both
 * layouts; only its surroundings differ.
 */
function LedgerIdentity({ entry }: { entry: WalletLedgerEntryResponse }) {
  return (
    <div className="flex min-w-0 flex-col gap-1.5">
      <Badge tone={sourceTone(entry.sourceType)} className="self-start">
        {sourceLabel(entry.sourceType)}
      </Badge>
      <span className="text-sm text-fg">{entry.description}</span>
      <span className="nums text-xs text-fg-subtle">{formatInstant(entry.createdAtUtc)}</span>
    </div>
  );
}

/**
 * The below-`md` card. Not a generic label:value stack: the amount is what a
 * customer opens this screen for, so it keeps its place on the same line as
 * the entry it belongs to, and only the running balance - the one value with
 * no meaning without its header - carries a visible label.
 */
function LedgerCardRow({ entry }: { entry: WalletLedgerEntryResponse }) {
  return (
    <li className="flex flex-col gap-2.5 px-4 py-3.5">
      <div className="flex items-start justify-between gap-3">
        <LedgerIdentity entry={entry} />
        <span className="shrink-0 text-right text-sm">
          <LedgerAmount entry={entry} />
        </span>
      </div>
      <dl className="flex items-baseline justify-between gap-3 border-t border-line/60 pt-2.5 text-xs">
        <dt className="font-medium uppercase tracking-wide text-fg-subtle">Balance after</dt>
        <dd className="nums text-sm text-fg-muted">{inr(entry.balanceAfter)}</dd>
      </dl>
    </li>
  );
}

function LedgerRow({ entry }: { entry: WalletLedgerEntryResponse }) {
  return (
    <TR>
      <TD>
        <LedgerIdentity entry={entry} />
      </TD>
      <TD numeric>
        <LedgerAmount entry={entry} />
      </TD>
      <TD numeric className="text-fg-muted">
        {inr(entry.balanceAfter)}
      </TD>
    </TR>
  );
}

function WalletIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.75"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-5 w-5"
      aria-hidden
    >
      <path d="M3 7.5A2.5 2.5 0 0 1 5.5 5H18a2 2 0 0 1 2 2v1" />
      <path d="M3 7.5V17a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-2" />
      <path d="M21 11h-4a2 2 0 0 0 0 4h4z" />
    </svg>
  );
}
