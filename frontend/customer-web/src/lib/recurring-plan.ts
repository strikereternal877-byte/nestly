/**
 * Numbers and wording shared by the screens that create and manage recurring plans.
 *
 * The limits mirror the server's `RecurringBookings:*` settings, which are the authority and reject anything beyond
 * them; they are held here so a screen can stop at the limit and name it. Keep them in step with the server.
 */

/** How many visits' worth of wallet balance a wallet-paid plan should keep - below this the customer is nudged to add money. (RecurringBookings:WalletLowBalanceVisits) */
export const WALLET_LOW_BALANCE_VISITS = 3;

/** Visits in a row that can go unpaid before a pay-as-you-go plan pauses itself. (RecurringBookings:PauseAfterUnpaidVisits) */
export const PAUSE_AFTER_UNPAID_VISITS = 2;

/** The furthest ahead, in days, a "skip visits until" date may be. (RecurringBookings:MaxSkipDays) */
export const MAX_SKIP_DAYS = 30;

/** How many times one plan may be told "skip visits until a date". (RecurringBookings:MaxSkipRangesPerPlan) */
export const MAX_SKIP_RANGES = 2;

/** "1 visit" / "3 visits". */
export function visitsLabel(count: number): string {
  return count === 1 ? "1 visit" : `${count} visits`;
}

/**
 * What it costs to cancel one already-booked visit, in one sentence: free until a number of hours before it starts,
 * then a percentage of what was paid. Comes from the platform's cancellation policy (sent with the plan), so it is the
 * same rule the cancel screen applies - and says "the usual policy" rather than a wrong number when it is not known.
 */
export function cancellationChargeSentence(freeWindowHours: number | null, feePercentage: number | null): string {
  if (freeWindowHours === null || feePercentage === null) {
    return "The usual cancellation policy applies.";
  }

  return `Cancelling a booked visit is free until ${Number(freeWindowHours)} hours before it starts; after that a ${Number(feePercentage)}% fee applies.`;
}
