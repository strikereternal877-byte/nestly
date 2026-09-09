/**
 * Read-only catalog fetching for server-rendered metadata and the sitemap.
 *
 * The base URL is read straight from the environment rather than imported
 * from `lib/api`: that module pulls in `lib/auth`, a `"use client"` module
 * built around sessionStorage, which has no business in the server graph. The
 * expression here is deliberately identical to the one there.
 *
 * Every caller renders a page that must not fail because the catalog API is
 * briefly unavailable, so failures return null and let the caller degrade.
 */
export const SERVER_API = `${process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5257"}/api/v1`;

export async function serverJson<T>(path: string, revalidate = 3600): Promise<T | null> {
  try {
    const response = await fetch(`${SERVER_API}${path}`, { next: { revalidate } });
    return response.ok ? ((await response.json()) as T) : null;
  } catch {
    return null;
  }
}
