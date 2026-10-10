"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { ErrorState } from "@/components/states";
import { Alert, Badge, Button, Card, Field, Skeleton, useToast } from "@/components/ui";
import { ApiError, describeError } from "@/lib/api";
import { formatDate } from "@/lib/format";
import { getBankAccount, submitBankAccount } from "@/lib/profile-api";
import type { BadgeTone } from "@/components/ui";
import { BankAccountVerificationStatus } from "@/lib/profile-types";
import type { BankAccount } from "@/lib/profile-types";

const VERIFICATION_STATUS_LABELS: Record<BankAccountVerificationStatus, string> = {
  [BankAccountVerificationStatus.Pending]: "Under review",
  [BankAccountVerificationStatus.Verified]: "Verified",
  [BankAccountVerificationStatus.Rejected]: "Rejected",
};

const VERIFICATION_STATUS_TONES: Record<BankAccountVerificationStatus, BadgeTone> = {
  [BankAccountVerificationStatus.Pending]: "warning",
  [BankAccountVerificationStatus.Verified]: "success",
  [BankAccountVerificationStatus.Rejected]: "danger",
};

// Standard 11-character Indian IFSC format: 4 letters (bank code), a
// literal '0', then 6 alphanumeric characters (branch code) - mirrors
// SubmitProviderBankAccountRequestValidator's IfscPattern server-side.
const IFSC_PATTERN = /^[A-Za-z]{4}0[A-Za-z0-9]{6}$/;

const bankAccountSchema = z.object({
  accountHolderName: z.string().min(1, "Account holder name is required").max(200),
  accountNumber: z
    .string()
    .min(1, "Account number is required")
    .regex(/^[0-9]{6,20}$/, "Account number must be 6 to 20 digits"),
  ifscCode: z
    .string()
    .min(1, "IFSC code is required")
    .regex(IFSC_PATTERN, "IFSC code must be 11 characters in the standard format (e.g. HDFC0001234)"),
  bankName: z.string().min(1, "Bank name is required").max(200),
});
type BankAccountFormValues = z.infer<typeof bankAccountSchema>;

const EMPTY_FORM: BankAccountFormValues = {
  accountHolderName: "",
  accountNumber: "",
  ifscCode: "",
  bankName: "",
};

function toFormValues(bankAccount: BankAccount): BankAccountFormValues {
  return {
    accountHolderName: bankAccount.accountHolderName,
    accountNumber: bankAccount.accountNumber,
    ifscCode: bankAccount.ifscCode,
    bankName: bankAccount.bankName,
  };
}

/**
 * Structured bank account details for payouts (docs/PROVIDER.md OPEN
 * DECISIONS #3) - sits alongside `KycSection`'s `BankAccountProof` document
 * upload (that photo stays supporting evidence; these structured fields are
 * what an admin actually uses to run a payout). One record, upserted in
 * place: unlike KYC documents there is no list, and resubmitting always
 * resets verification back to Pending - the form below is simultaneously the
 * "add" and "edit" affordance.
 *
 * A 404 from `getBankAccount` means nothing has been submitted yet - the same
 * status-code-as-signal idiom `isNotImplemented` uses elsewhere in this
 * client - so it renders the empty form rather than `ErrorState`.
 */
export function BankAccountSection() {
  const queryClient = useQueryClient();
  const toast = useToast();

  const query = useQuery({ queryKey: ["provider-bank-account"], queryFn: getBankAccount, retry: false });
  const notSubmittedYet = query.isError && query.error instanceof ApiError && query.error.status === 404;
  const bankAccount = query.isSuccess ? query.data : null;

  const form = useForm<BankAccountFormValues>({
    resolver: zodResolver(bankAccountSchema),
    defaultValues: EMPTY_FORM,
  });

  // Re-seeds the form whenever the loaded bank account changes (e.g. after a
  // successful submit refetches) - mirrors ProfileDetailsSection/CapacityEditor's
  // "seed from server, then let the provider edit freely" pattern elsewhere
  // in this app, rather than controlling every field from page state.
  useEffect(() => {
    if (bankAccount) {
      form.reset(toFormValues(bankAccount));
    }
  }, [bankAccount, form]);

  const mutation = useMutation({
    mutationFn: (values: BankAccountFormValues) =>
      submitBankAccount({
        accountHolderName: values.accountHolderName.trim(),
        accountNumber: values.accountNumber.trim(),
        ifscCode: values.ifscCode.trim().toUpperCase(),
        bankName: values.bankName.trim(),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["provider-bank-account"] });
      toast("success", "Bank account details submitted for review.");
    },
    onError: (error) => toast("error", describeError(error)),
  });

  const onSubmit = form.handleSubmit((values) => mutation.mutate(values));

  return (
    <Card
      title="Bank account"
      description="Structured account details used to process your payouts - separate from the bank proof document above."
    >
      <div className="flex flex-col gap-5">
        {query.isPending ? (
          <Skeleton className="h-20 w-full rounded-xl" aria-hidden />
        ) : query.isError && !notSubmittedYet ? (
          <ErrorState
            title="Couldn't load your bank account details"
            error={query.error}
            onRetry={() => query.refetch()}
            isRetrying={query.isRefetching}
          />
        ) : bankAccount ? (
          <BankAccountSummary bankAccount={bankAccount} />
        ) : (
          <Alert tone="info" title="No bank account submitted yet">
            Add your account details below so payouts can be processed once verified.
          </Alert>
        )}

        <form onSubmit={onSubmit} className="flex flex-col gap-4 border-t border-line pt-5" noValidate>
          <h3 className="text-sm font-semibold text-fg">
            {bankAccount ? "Update your bank account" : "Add your bank account"}
          </h3>

          {mutation.isError ? (
            <ErrorState title="Couldn't submit those details" error={mutation.error} />
          ) : null}

          <div className="grid gap-4 sm:grid-cols-2">
            <Field
              label="Account holder name"
              error={form.formState.errors.accountHolderName?.message}
              {...form.register("accountHolderName")}
            />
            <Field
              label="Bank name"
              error={form.formState.errors.bankName?.message}
              {...form.register("bankName")}
            />
          </div>

          <div className="grid gap-4 sm:grid-cols-2">
            <Field
              label="Account number"
              inputMode="numeric"
              error={form.formState.errors.accountNumber?.message}
              {...form.register("accountNumber")}
            />
            <Field
              label="IFSC code"
              placeholder="HDFC0001234"
              hint="11 characters, e.g. HDFC0001234."
              error={form.formState.errors.ifscCode?.message}
              {...form.register("ifscCode")}
            />
          </div>

          <Button type="submit" size="lg" fullWidth loading={mutation.isPending}>
            {bankAccount ? "Save and resubmit for review" : "Submit for review"}
          </Button>
        </form>
      </div>
    </Card>
  );
}

/** Current verification status, plus the rejection reason when there is one to act on - mirrors KycSection's VerificationSummary. */
function BankAccountSummary({ bankAccount }: { bankAccount: BankAccount }) {
  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-start justify-between gap-3 rounded-xl border border-line bg-surface-2 p-3.5">
        <div className="min-w-0">
          <p className="text-sm font-medium text-fg">{bankAccount.bankName}</p>
          <p className="nums mt-0.5 truncate text-xs text-fg-muted">
            {bankAccount.accountHolderName} · •••• {bankAccount.accountNumber.slice(-4)} · {bankAccount.ifscCode}
          </p>
          <p className="nums mt-2 text-xs text-fg-subtle">
            Last updated {formatDate(bankAccount.updatedAt)}
            {bankAccount.verifiedAt ? ` · Reviewed ${formatDate(bankAccount.verifiedAt)}` : ""}
          </p>
        </div>
        <Badge tone={VERIFICATION_STATUS_TONES[bankAccount.verificationStatus]}>
          {VERIFICATION_STATUS_LABELS[bankAccount.verificationStatus]}
        </Badge>
      </div>

      {bankAccount.verificationStatus === BankAccountVerificationStatus.Rejected ? (
        <Alert tone="error" title="Bank account details rejected">
          {bankAccount.rejectionReason ?? "Update the details below and resubmit."}
        </Alert>
      ) : null}
    </div>
  );
}
