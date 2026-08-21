/**
 * Thin HTTP helpers for the harness's *setup* phase (task #387).
 *
 * Load generation itself is k6's job - nothing in this file runs under load.
 * This is the seeding path, and it deliberately mirrors the conventions of
 * `frontend/customer-web/e2e/setup/seed-catalog.ts`: build fixtures through
 * the real admin/consumer APIs rather than inserting rows, and look an entity
 * up before creating it (several of these tables have no unique constraint on
 * the fields we key off, so create-then-409 silently accumulates duplicates
 * on every re-run).
 */

export const ADMIN_API = process.env.ADMIN_API_URL ?? "http://localhost:5177";
export const CONSUMER_API = process.env.CONSUMER_API_URL ?? "http://localhost:5257";

async function request(method, url, token, body) {
  const headers = { Accept: "application/json" };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined && body !== null) headers["Content-Type"] = "application/json";

  const res = await fetch(url, {
    method,
    headers,
    body: body === undefined || body === null ? undefined : JSON.stringify(body),
  });

  if (!res.ok) {
    throw new Error(`${method} ${url} failed: ${res.status} ${await res.text()}`);
  }
  if (res.status === 204) return null;
  const text = await res.text();
  return text.length === 0 ? null : JSON.parse(text);
}

export const get = (url, token) => request("GET", url, token);
export const post = (url, token, body) => request("POST", url, token, body);
export const put = (url, token, body) => request("PUT", url, token, body);
export const patch = (url, token, body) => request("PATCH", url, token, body);

/** Finds an entity in a GET-list response, creating it via POST only when genuinely absent. */
export async function findOrCreate(token, listUrl, createUrl, createBody, predicate) {
  const existing = (await get(listUrl, token)).find(predicate);
  if (existing) return existing;
  return post(createUrl, token, createBody);
}

export async function adminLogin() {
  const body = await post(`${ADMIN_API}/api/v1/admin/auth/login`, null, {
    email: process.env.LOAD_ADMIN_EMAIL ?? "dev-admin@nestly.local",
    password: process.env.LOAD_ADMIN_PASSWORD ?? "E2eTest!Passw0rd",
  });
  return body.accessToken;
}

export async function customerLogin(email, password) {
  const body = await post(`${CONSUMER_API}/api/v1/auth/login/password`, null, { email, password });
  return body.accessToken;
}

/** Reads a JWT's `exp` claim without verifying it - used only to decide whether a cached token is still usable. */
export function tokenExpiryMs(jwt) {
  try {
    const payload = JSON.parse(Buffer.from(jwt.split(".")[1], "base64url").toString("utf8"));
    return typeof payload.exp === "number" ? payload.exp * 1000 : 0;
  } catch {
    return 0;
  }
}

export async function waitForHealthy(url, attempts = 60, delayMs = 2000) {
  for (let i = 1; i <= attempts; i += 1) {
    try {
      const res = await fetch(url);
      if (res.ok) return;
    } catch {
      /* not up yet */
    }
    await new Promise((resolve) => setTimeout(resolve, delayMs));
  }
  throw new Error(`${url} did not become ready`);
}
