import type { Metadata } from "next";

import { serverJson } from "@/lib/server-api";
import type { ServiceDetail } from "@/lib/types";

import ServiceDetailClient from "./ServiceDetailClient";

/**
 * Server shell for the service detail page.
 *
 * The page itself stays a client component - it is built on TanStack Query,
 * the city selector and the price calculator, none of which render on the
 * server. This wrapper exists purely so the route can emit a real title and
 * description: a client component cannot export `generateMetadata`, which is
 * why every catalog URL previously shared the site-wide default title.
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

export default function ServiceDetailPage() {
  return <ServiceDetailClient />;
}
