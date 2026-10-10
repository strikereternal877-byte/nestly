/**
 * Getting the customer's position in ONE tap of "Allow location".
 *
 * Reported: on a customer's first "Allow location" tap nothing comes back, and tapping the same button again
 * works. Two things made that happen, and both come from asking for a position while the browser's own
 * permission question is still open:
 *
 * - `getCurrentPosition`'s timeout runs from the moment the call is made, not from when the customer taps Allow on
 *   the browser's prompt. Add the time a person takes to read and answer that prompt to a cold GPS start and the
 *   8s high-accuracy and 10s coarse budgets can both be spent before any fix exists - a timeout, not a refusal.
 * - Some browsers - an embedded one, or one whose prompt is up - fail the call at once (PERMISSION_DENIED) while the
 *   question is still unanswered. The old code treated that as the customer's real "no" and gave up, so the second
 *   tap, made after they had answered, was the one that worked.
 *
 * So the question is separated from the fix. `ensureLocationPermission` asks, then waits - with no short timer
 * running against the customer - for the permission state to turn granted (or denied), and only then does the
 * position get requested, with the same high-accuracy-then-coarse budgets as before. A refusal is recognised by the
 * permission state actually being "denied", not by the first error that happens to carry code 1, so a real "no" still
 * stops at once and is never retried or nagged.
 *
 * A third cause shows up on a phone whose location services have only just been switched on (or whose GPS is cold):
 * the first attempts fail at once with POSITION_UNAVAILABLE and an instant retry fails the same way, while a tap a few
 * seconds later succeeds. Retries are therefore spaced out (1s, then 2s) and stop at an overall deadline.
 *
 * Browsers without the Permissions API (older Safari) cannot report the state; they skip the wait and just get the
 * spaced retries: timeouts and "unavailable" are retried, an explicit denial is not.
 */

/** `GeolocationPositionError.code` for an explicit "no" - the one failure a retry can never turn into a yes. */
export const GEOLOCATION_PERMISSION_DENIED = 1;

/** `GeolocationPositionError.code` for a timeout. */
export const GEOLOCATION_TIMEOUT = 3;

/** How long to wait for the customer to answer the browser's location prompt before giving up. */
const DEFAULT_PERMISSION_WAIT_MS = 60_000;

/** Time a fix may take once permission is settled: long enough to catch a real GPS lock, short enough not to stall an indoor iOS customer. */
const HIGH_ACCURACY_TIMEOUT_MS = 8_000;

/** Budget of the coarse (WiFi / cell tower) attempt - fast by nature, so much shorter than the original 20s. */
const COARSE_TIMEOUT_MS = 10_000;

/** Pauses before the 2nd and 3rd round of attempts: a cold location provider needs seconds, not milliseconds. */
const DEFAULT_RETRY_DELAYS_MS = [1_000, 2_000];

/** No new round of attempts starts after this long, so a customer is never left spinning for ever. */
const POSITION_DEADLINE_MS = 30_000;

/** A fix this recent is as good as a new one for picking a city, and returns instantly. */
const ACCEPTABLE_CACHED_FIX_AGE_MS = 5 * 60 * 1000;

/** The slice of the browser this module needs, injectable so the permission handling can be unit tested. */
export interface GeolocationEnvironment {
  geolocation: Pick<Geolocation, "getCurrentPosition">;
  /** Absent where the Permissions API is: the flow then relies on `getCurrentPosition` alone. */
  permissions?: Pick<Permissions, "query">;
}

/** Where the flow is, so the screen can say "tap Allow on the browser's prompt" while it waits. */
export type LocatePhase = "awaiting-permission" | "locating";

export interface LocateOptions {
  onPhase?: (phase: LocatePhase) => void;
  /** How long to wait for the customer to answer the browser's prompt. */
  permissionWaitMs?: number;
  /** Pauses between rounds of attempts (one more round than there are delays). */
  retryDelaysMs?: readonly number[];
}

export function browserGeolocationEnvironment(): GeolocationEnvironment {
  return { geolocation: navigator.geolocation, permissions: navigator.permissions };
}

export function isPermissionDenied(error: unknown): boolean {
  return typeof error === "object" && error !== null && "code" in error && (error as { code: unknown }).code === GEOLOCATION_PERMISSION_DENIED;
}

function positionError(code: number, message: string): Error & { code: number } {
  return Object.assign(new Error(message), { code });
}

async function queryPermission(environment: GeolocationEnvironment): Promise<PermissionStatus | null> {
  if (!environment.permissions) return null;

  try {
    return await environment.permissions.query({ name: "geolocation" });
  } catch {
    // Some browsers throw for a permission name they do not support: treat that as "cannot tell".
    return null;
  }
}

/**
 * Resolves once location permission is granted, rejects with a code-1 error when it is denied (before or while
 * waiting) and with a code-3 error when the customer never answers. Resolves at once when the state cannot be read.
 */
export async function ensureLocationPermission(environment: GeolocationEnvironment, options: LocateOptions = {}): Promise<void> {
  const status = await queryPermission(environment);
  if (status === null || status.state === "granted") return;

  if (status.state === "denied") {
    throw positionError(GEOLOCATION_PERMISSION_DENIED, "Location permission is denied.");
  }

  const waitMs = options.permissionWaitMs ?? DEFAULT_PERMISSION_WAIT_MS;
  options.onPhase?.("awaiting-permission");

  await new Promise<void>((resolve, reject) => {
    let settled = false;

    const settle = (outcome: () => void) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      status.removeEventListener("change", recheck);
      outcome();
    };

    // The answer is read from the permission state itself. An error from the call below is only a reason to look
    // again: a browser that fails it at once while its prompt is still open leaves the state at "prompt".
    const recheck = () => {
      if (status.state === "granted") {
        settle(resolve);
      } else if (status.state === "denied") {
        settle(() => reject(positionError(GEOLOCATION_PERMISSION_DENIED, "Location permission was denied.")));
      }
    };

    const timer = setTimeout(
      () => settle(() => reject(positionError(GEOLOCATION_TIMEOUT, "Timed out waiting for the location permission prompt."))),
      waitMs,
    );

    status.addEventListener("change", recheck);

    // Asking is what makes the browser show its prompt. A returned position proves the answer was yes.
    environment.geolocation.getCurrentPosition(() => settle(resolve), recheck, {
      maximumAge: ACCEPTABLE_CACHED_FIX_AGE_MS,
      timeout: waitMs,
    });
  });
}

/**
 * Requests a fix, preferring a high-accuracy one but never letting the accuracy request itself become a hard
 * failure. `enableHighAccuracy: true` holds out for a real GPS-chip lock (needed for building-level precision) but
 * on iOS WebKit it can time out far more often than on Android, especially indoors; on failure one coarse attempt
 * without it almost always succeeds.
 */
function getPositionWithFallback(environment: GeolocationEnvironment): Promise<GeolocationPosition> {
  return new Promise((resolve, reject) => {
    environment.geolocation.getCurrentPosition(
      resolve,
      (error) => {
        // A refusal is final for this attempt too: a coarse request would only be refused again.
        if (isPermissionDenied(error)) {
          reject(error);
          return;
        }

        environment.geolocation.getCurrentPosition(resolve, reject, {
          enableHighAccuracy: false,
          timeout: COARSE_TIMEOUT_MS,
        });
      },
      { enableHighAccuracy: true, timeout: HIGH_ACCURACY_TIMEOUT_MS },
    );
  });
}

const pause = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * Further rounds of attempts after a failed one, spaced out, except for an explicit denial - that is the customer's
 * answer, and asking again would look like nagging past a "no". Rejects with the last round's error.
 */
async function getPositionWithRetry(environment: GeolocationEnvironment, retryDelaysMs: readonly number[]): Promise<GeolocationPosition> {
  const startedAt = Date.now();
  let lastError: unknown;

  for (let round = 0; round <= retryDelaysMs.length; round++) {
    if (round > 0) {
      await pause(retryDelaysMs[round - 1]);
    }

    try {
      return await getPositionWithFallback(environment);
    } catch (error) {
      if (isPermissionDenied(error)) throw error;
      lastError = error;
      if (Date.now() - startedAt > POSITION_DEADLINE_MS) break;
    }
  }

  throw lastError;
}

/** Asks for permission if it is still open, waits for the answer, then returns the customer's position. */
export async function locateCustomer(
  environment: GeolocationEnvironment = browserGeolocationEnvironment(),
  options: LocateOptions = {},
): Promise<GeolocationPosition> {
  await ensureLocationPermission(environment, options);
  options.onPhase?.("locating");
  return getPositionWithRetry(environment, options.retryDelaysMs ?? DEFAULT_RETRY_DELAYS_MS);
}
