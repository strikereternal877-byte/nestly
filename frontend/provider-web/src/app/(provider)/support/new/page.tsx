"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { useRef } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { ErrorState } from "@/components/states";
import { Button, Card, Field, PageHeading, Select, Textarea } from "@/components/ui";
import { createSupportTicket } from "@/lib/support-api";
import { ProviderSupportTicketCategory } from "@/lib/support-types";

const SUBJECT_MAX = 200;
const DESCRIPTION_MAX = 4000;

const CATEGORY_OPTIONS = [
  { value: String(ProviderSupportTicketCategory.Payout), label: "Payout" },
  { value: String(ProviderSupportTicketCategory.Kyc), label: "KYC / documents" },
  { value: String(ProviderSupportTicketCategory.JobIssue), label: "Job issue" },
  { value: String(ProviderSupportTicketCategory.Account), label: "Account" },
  { value: String(ProviderSupportTicketCategory.Technical), label: "App / technical" },
  { value: String(ProviderSupportTicketCategory.Other), label: "Other" },
];
const CATEGORY_VALUES = CATEGORY_OPTIONS.map((option) => option.value);

const createTicketSchema = z.object({
  category: z.string().refine((value) => CATEGORY_VALUES.includes(value), "Choose a category"),
  subject: z
    .string()
    .trim()
    .min(1, "Subject is required")
    .max(SUBJECT_MAX, `Subject must be ${SUBJECT_MAX} characters or fewer`),
  description: z
    .string()
    .trim()
    .min(1, "Description is required")
    .max(DESCRIPTION_MAX, `Description must be ${DESCRIPTION_MAX} characters or fewer`),
});
type CreateTicketFormValues = z.infer<typeof createTicketSchema>;

/** Raise a new support ticket (Provider Management UX pass). */
export default function NewSupportTicketPage() {
  const router = useRouter();

  const form = useForm<CreateTicketFormValues>({
    resolver: zodResolver(createTicketSchema),
    defaultValues: { category: String(ProviderSupportTicketCategory.Other), subject: "", description: "" },
  });
  const { errors } = form.formState;
  const descriptionLength = form.watch("description").length;

  // Same double-submit guard as customer-web's own new-ticket form: the
  // submit button's disabled state can lag a second click by a tick, and the
  // cost of losing that race is a duplicate ticket in the queue.
  const inFlight = useRef(false);

  const createMutation = useMutation({
    mutationFn: (values: CreateTicketFormValues) =>
      createSupportTicket({
        category: Number(values.category) as ProviderSupportTicketCategory,
        subject: values.subject.trim(),
        description: values.description.trim(),
      }),
    onSuccess: (ticket) => router.push(`/support/${ticket.id}`),
    onError: () => {
      inFlight.current = false;
    },
  });

  const onSubmit = form.handleSubmit((values) => {
    if (inFlight.current) return;
    inFlight.current = true;
    createMutation.mutate(values);
  });

  return (
    <div className="flex w-full max-w-2xl animate-rise flex-col gap-6">
      <PageHeading title="Raise a ticket" subtitle="Tell us what's wrong and we'll get back to you." />

      <Card>
        <form onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
          {createMutation.isError ? (
            <ErrorState title="Couldn't create your ticket" error={createMutation.error} />
          ) : null}

          <Select label="Category" options={CATEGORY_OPTIONS} error={errors.category?.message} {...form.register("category")} />

          <Field
            label="Subject"
            required
            maxLength={SUBJECT_MAX}
            error={errors.subject?.message}
            {...form.register("subject")}
          />

          <Textarea
            label="Description"
            rows={6}
            required
            maxLength={DESCRIPTION_MAX}
            hint={`${descriptionLength} of ${DESCRIPTION_MAX} characters`}
            error={errors.description?.message}
            {...form.register("description")}
          />

          <Button type="submit" className="self-start" loading={createMutation.isPending}>
            Submit ticket
          </Button>
        </form>
      </Card>
    </div>
  );
}
