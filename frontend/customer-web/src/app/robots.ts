import type { MetadataRoute } from "next";

import { siteUrl } from "@/lib/site-url";

/**
 * Crawl rules for the public storefront. Everything behind sign-in is
 * disallowed - those routes only ever redirect a crawler to /login, so
 * letting them be fetched wastes crawl budget and surfaces nothing.
 */
export default function robots(): MetadataRoute.Robots {
  return {
    rules: {
      userAgent: "*",
      allow: "/",
      disallow: [
        "/addresses/",
        "/amc/",
        "/booking/",
        "/bookings/",
        "/login",
        "/profile/",
        "/recurring-bookings/",
        "/refer-earn",
        "/register",
        "/subscription/",
        "/support/",
        "/wallet",
      ],
    },
    sitemap: `${siteUrl()}/sitemap.xml`,
  };
}
