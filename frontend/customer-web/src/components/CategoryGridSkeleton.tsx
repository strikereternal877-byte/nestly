import { Skeleton } from "@/components/ui";

/**
 * Loading placeholder for a category tile grid.
 *
 * Shared by the home tiles, the category listing and search results so all
 * three reserve the same shape as the real grid — a skeleton whose dimensions
 * don't match what replaces it causes a layout jump, which is worse than
 * showing nothing at all.
 */
export function CategoryGridSkeleton({ count = 8 }: { count?: number }) {
  return (
    <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 sm:gap-6 lg:grid-cols-4 lg:gap-8">
      {Array.from({ length: count }, (_, index) => (
        <div key={index} className="flex flex-col gap-3">
          <Skeleton className="aspect-[4/5] w-full rounded-2xl" />
          <Skeleton className="h-4 w-2/3" />
        </div>
      ))}
    </div>
  );
}
