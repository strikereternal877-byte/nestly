"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Alert, Button, Card, Field, Select } from "@/components/ui";
import { describeError } from "@/lib/api";
import { getKycStatus, submitKycDocument } from "@/lib/profile-api";
import type { KycDocType } from "@/lib/profile-types";

const DOC_TYPE_OPTIONS: { value: KycDocType; label: string }[] = [
  { value: "IdentityProof", label: "Identity proof" },
  { value: "AddressProof", label: "Address proof" },
  { value: "BankAccountProof", label: "Bank account proof" },
  { value: "ProfessionalCertificate", label: "Professional certificate" },
  { value: "Other", label: "Other" },
];

const VERIFICATION_STATUS_LABELS: Record<string, string> = {
  Pending: "Pending review",
  Approved: "Approved",
  Rejected: "Rejected",
};

const kycDocumentSchema = z.object({
  docType: z.enum(["IdentityProof", "AddressProof", "BankAccountProof", "ProfessionalCertificate", "Other"]),
  fileRef: z.string().min(1, "Provide a file reference").max(2000),
  docNumber: z.string().max(100),
});
type KycDocumentFormValues = z.infer<typeof kycDocumentSchema>;

/**
 * KYC status and document submission (docs/PROVIDER.md's Identity domain,
 * `provider_kyc_document`). There is no file storage backend wired up on the
 * platform yet, so `fileRef` is a plain text reference/URL the provider
 * pastes in (e.g. a link to a document already hosted elsewhere) rather than
 * a real upload - the `<input type="file">` below is NOT wired to any
 * upload endpoint; it only lets a provider pick a local file to read its name
 * as a starting point for the reference field, which they can still edit.
 */
export function KycSection() {
  const queryClient = useQueryClient();

  const query = useQuery({ queryKey: ["provider-kyc"], queryFn: getKycStatus });

  const form = useForm<KycDocumentFormValues>({
    resolver: zodResolver(kycDocumentSchema),
    defaultValues: { docType: "IdentityProof", fileRef: "", docNumber: "" },
  });

  const mutation = useMutation({
    mutationFn: (values: KycDocumentFormValues) =>
      submitKycDocument({
        docType: values.docType,
        fileRef: values.fileRef,
        docNumber: values.docNumber.trim() === "" ? undefined : values.docNumber,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["provider-kyc"] });
      form.reset({ docType: "IdentityProof", fileRef: "", docNumber: "" });
    },
  });

  const onSubmit = form.handleSubmit((values) => mutation.mutate(values));

  return (
    <Card title="KYC documents" description="Identity and verification documents required before you can go live.">
      {query.isPending ? (
        <p className="text-sm text-neutral-500">Loading KYC status…</p>
      ) : query.isError ? (
        <Alert>{describeError(query.error)}</Alert>
      ) : query.data.documents.length === 0 ? (
        <p className="text-sm text-neutral-600 dark:text-neutral-400">No documents submitted yet.</p>
      ) : (
        <div className="overflow-x-auto rounded-xl border border-black/10 dark:border-white/15">
          <table className="w-full min-w-[640px] text-left text-sm">
            <thead className="border-b border-black/10 bg-black/5 dark:border-white/15 dark:bg-white/5">
              <tr>
                <th className="px-3 py-2 font-medium">Type</th>
                <th className="px-3 py-2 font-medium">Doc number</th>
                <th className="px-3 py-2 font-medium">Status</th>
                <th className="px-3 py-2 font-medium">Submitted</th>
              </tr>
            </thead>
            <tbody>
              {query.data.documents.map((doc) => (
                <tr key={doc.id} className="border-b border-black/5 last:border-0 dark:border-white/10">
                  <td className="px-3 py-2">{DOC_TYPE_OPTIONS.find((o) => o.value === doc.docType)?.label ?? doc.docType}</td>
                  <td className="px-3 py-2">{doc.docNumber ?? "—"}</td>
                  <td className="px-3 py-2">{VERIFICATION_STATUS_LABELS[doc.verificationStatus] ?? doc.verificationStatus}</td>
                  <td className="px-3 py-2">{new Date(doc.submittedAt).toLocaleDateString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <form onSubmit={onSubmit} className="mt-5 flex flex-col gap-3 border-t border-black/10 pt-4 dark:border-white/15" noValidate>
        <h3 className="text-sm font-semibold">Submit a document</h3>
        {mutation.isError ? <Alert>{describeError(mutation.error)}</Alert> : null}
        <div className="flex flex-wrap items-end gap-3">
          <div className="w-56">
            <Select label="Document type" options={DOC_TYPE_OPTIONS} {...form.register("docType")} />
          </div>
          <div className="w-56">
            <Field
              label="Document number (optional)"
              error={form.formState.errors.docNumber?.message}
              {...form.register("docNumber")}
            />
          </div>
        </div>
        <Field
          label="File reference (URL or reference ID)"
          placeholder="https://…"
          error={form.formState.errors.fileRef?.message}
          {...form.register("fileRef")}
        />
        {/* Not wired to any upload endpoint - file storage does not exist on the
            platform yet. This only lets a provider conveniently copy a local
            file's name into the reference field above; nothing is uploaded. */}
        <div>
          <label className="mb-1.5 block text-sm font-medium">
            Pick a local file (name only, not uploaded)
          </label>
          <input
            type="file"
            onChange={(event) => {
              const fileName = event.target.files?.[0]?.name;
              if (fileName) form.setValue("fileRef", fileName);
            }}
            className="block text-sm"
          />
        </div>
        <div>
          <Button type="submit" disabled={mutation.isPending}>
            {mutation.isPending ? "Submitting…" : "Submit document"}
          </Button>
        </div>
      </form>
    </Card>
  );
}
