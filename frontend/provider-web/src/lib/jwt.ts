/**
 * Best-effort, unverified decode of a JWT's payload segment.
 *
 * This is deliberately NOT signature verification - a browser has no way to
 * hold the signing secret, and verifying a signature client-side would prove
 * nothing anyway (anyone can read the source). This exists purely so the UI
 * can read claims (role, permissions) to decide what to *show*; every
 * request that matters is re-checked by the API against the real token.
 * Never use the return value of this function for an authorization
 * decision - only for rendering.
 */
export function decodeJwtPayload<T>(token: string): T | null {
  const segments = token.split(".");
  if (segments.length !== 3) return null;

  try {
    const base64Url = segments[1];
    const base64 = base64Url.replace(/-/g, "+").replace(/_/g, "/");
    const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), "=");
    const json = atob(padded);
    return JSON.parse(json) as T;
  } catch {
    // Malformed or non-JWT token; callers treat a null result as "no claims".
    return null;
  }
}
