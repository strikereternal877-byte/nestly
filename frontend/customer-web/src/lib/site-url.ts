/**
 * Absolute public origin of this storefront, for the crawl files that must
 * emit fully-qualified URLs (robots.txt, sitemap.xml).
 *
 * `NEXT_PUBLIC_SITE_URL` wins when set. Otherwise Vercel's own
 * `VERCEL_PROJECT_PRODUCTION_URL` (the stable production domain, not the
 * per-deployment one) keeps production correct with no configuration at all.
 * Localhost is the last resort so `next dev` still serves valid files.
 */
export function siteUrl(): string {
  const configured = process.env.NEXT_PUBLIC_SITE_URL;
  if (configured) return configured.replace(/\/$/, "");

  const vercel = process.env.VERCEL_PROJECT_PRODUCTION_URL;
  if (vercel) return `https://${vercel}`;

  return "http://localhost:3000";
}
