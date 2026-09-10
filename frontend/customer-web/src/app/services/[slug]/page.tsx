import type { Metadata } from "next";
import Link from "next/link";

import { PageBanner } from "@/components/PageBanner";
import { serverJson } from "@/lib/server-api";
import type { ServiceDetail } from "@/lib/types";

import ServiceDetailClient from "./ServiceDetailClient";

/**
 * Server shell for the service detail page (SEO fix, docs/OPEN-FIXES-FEATURES.csv
 * "Service + category pages, Server-side rendering").
 *
 * `generateMetadata` already server-rendered the title/description; this
 * fetches the same service (Next dedupes the identical `fetch` call via
 * Request Memoization, so it is not a second round-trip) and renders the
 * name, description, cover image and price in the initial HTML too, via
 * `PageBanner` - previously only the client-rendered `ServiceDetailClient`
 * had this content, invisible to crawlers/link-unfurlers that don't execute
 * JS. Everything that needs client state (city selection, serviceability,
 * the slot picker, price calculator, "Book now" CTA) stays inside
 * `ServiceDetailClient`, which receives the already-fetched service as
 * `initialService` so it renders immediately instead of re-fetching and
 * showing its loading skeleton.
 */
export async function generateMetadata({
  params,
}: {
  params: { slug: string };
}): Promise<Metadata> {
  const service = await serverJson<ServiceDetail>(`/services/${params.slug}`);
  if (!service) return {};

  return {
    title: service.name,
    description: service.description,
    alternates: { canonical: `/services/${service.slug}` },
    openGraph: {
      title: service.name,
      description: service.description,
      type: "website",
      ...(service.coverImageUrl ? { images: [service.coverImageUrl] } : {}),
    },
  };
}

export default async function ServiceDetailPage({
  params,
}: {
  params: { slug: string };
}) {
  const service = await serverJson<ServiceDetail>(`/services/${params.slug}`);

  return (
    <main className="flex w-full flex-col animate-rise">
      {service ? (
        <PageBanner
          title={service.name}
          description={service.description}
          imageUrl={service.coverImageUrl}
          breadcrumb={
            <Breadcrumb
              categoryName={service.categoryName}
              categorySlug={service.categorySlug}
              serviceName={service.name}
            />
          }
          badge={
            <span className="mt-1 inline-flex items-center gap-1.5 rounded-full bg-white/15 px-3.5 py-1.5 text-xs font-semibold text-white backdrop-blur-sm">
              Starts at <span className="nums">₹{service.price}</span>
            </span>
          }
        />
      ) : null}

      <ServiceDetailClient initialService={service} slug={params.slug} />
    </main>
  );
}

/**
 * Breadcrumb for the full-bleed `PageBanner`. Moved here from
 * `ServiceDetailClient` alongside the banner - it is static content sourced
 * from the same server-fetched service, not interactive.
 */
function Breadcrumb({
  categoryName,
  categorySlug,
  serviceName,
}: {
  categoryName: string;
  categorySlug: string;
  serviceName: string;
}) {
  return (
    <nav aria-label="Breadcrumb" className="text-sm">
      <ol className="flex flex-wrap items-center gap-1.5 text-white/70">
        <li>
          <Link href="/" className="hover:text-white">
            Home
          </Link>
        </li>
        <li aria-hidden>/</li>
        <li>
          <Link href="/categories" className="hover:text-white">
            Categories
          </Link>
        </li>
        <li aria-hidden>/</li>
        <li>
          <Link href={`/categories/${categorySlug}`} className="hover:text-white">
            {categoryName}
          </Link>
        </li>
        <li aria-hidden>/</li>
        <li className="truncate font-medium text-white" aria-current="page">
          {serviceName}
        </li>
      </ol>
    </nav>
  );
}
