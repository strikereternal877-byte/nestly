import type { Metadata } from "next";

import { serverJson } from "@/lib/server-api";
import type { CategoryDetail } from "@/lib/types";

import CategoryDetailClient from "./CategoryDetailClient";

/**
 * Server shell for the category detail page - same reasoning as the service
 * route: the listing stays a client component, and this wrapper exists only
 * so the URL can carry a real title and description instead of the site-wide
 * default.
 */
export async function generateMetadata({
  params,
}: {
  params: { slug: string };
}): Promise<Metadata> {
  const category = await serverJson<CategoryDetail>(`/categories/${params.slug}`);
  if (!category) return {};

  return {
    title: category.name,
    description: category.description,
    alternates: { canonical: `/categories/${category.slug}` },
    openGraph: {
      title: category.name,
      description: category.description,
      type: "website",
      ...(category.bannerUrl ? { images: [category.bannerUrl] } : {}),
    },
  };
}

export default function CategoryDetailPage() {
  return <CategoryDetailClient />;
}
