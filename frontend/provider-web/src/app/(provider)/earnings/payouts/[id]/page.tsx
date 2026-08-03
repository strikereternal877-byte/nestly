"use client";

import { useQuery } from "@tanstack/react-query";
import { useParams, useRouter } from "next/navigation";
import { Alert, Button, Card, PageHeading } from "@/components/ui";
import { describeError, isNotImplemented } from "@/lib/api";
import { getPayoutDetail } from "@/lib/earnings-api";
import { payoutStatusLabel } from "@/lib/earnings-types";

/** Single payout detail (docs/PROVIDER.md's `provider_payout` table). Same 501-stub caveat as the earnings list. */
export default function PayoutDetailPage() {
  const params = useParams<{ id: string }>();
  const payoutId = params.id;
  const router = useRouter();

  const query = useQuery({ queryKey: ["provider-payout", payoutId], queryFn: () => getPayoutDetail(payoutId) });

  return (
    <div className="mx-auto w-full max-w-2xl">
      <PageHeading title="Payout detail" />

      {query.isPending ? (
        <p className="text-sm text-neutral-500">Loading payout…</p>
      ) : query.isError && isNotImplemented(query.error) ? (
        <Card title="This payout isn't available yet">
          <p className="text-sm text-neutral-600 dark:text-neutral-400">
            Earnings tracking is still being built on the platform side. Check back once payouts go live.
          </p>
          <div className="mt-4">
            <Button type="button" variant="secondary" onClick={() => router.push("/earnings")}>
              Back to earnings
            </Button>
          </div>
        </Card>
      ) : query.isError ? (
        <Alert>{describeError(query.error)}</Alert>
      ) : (
        <Card title={`Payout ${query.data.id}`}>
          <dl className="grid grid-cols-1 gap-3 text-sm sm:grid-cols-2">
            <div>
              <dt className="font-medium">Status</dt>
              <dd>{payoutStatusLabel(query.data.status)}</dd>
            </div>
            <div>
              <dt className="font-medium">Total amount</dt>
              <dd>{query.data.totalAmount !== undefined ? `₹${query.data.totalAmount.toFixed(2)}` : "—"}</dd>
            </div>
            <div>
              <dt className="font-medium">Period</dt>
              <dd>
                {query.data.periodStart && query.data.periodEnd
                  ? `${query.data.periodStart} – ${query.data.periodEnd}`
                  : "—"}
              </dd>
            </div>
            <div>
              <dt className="font-medium">Payout reference</dt>
              <dd>{query.data.payoutReference ?? "Not yet issued"}</dd>
            </div>
          </dl>
        </Card>
      )}
    </div>
  );
}
