"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { Alert, Button, Card, Field } from "@/components/ui";
import { describeError } from "@/lib/api";
import { getProfile, updateProfile } from "@/lib/profile-api";

const profileSchema = z.object({
  legalName: z.string().min(1, "Legal name is required").max(200),
  displayName: z.string().min(1, "Display name is required").max(100),
  email: z.union([z.email("Enter a valid email address"), z.literal("")]),
});
type ProfileFormValues = z.infer<typeof profileSchema>;

const STATUS_LABELS: Record<string, string> = {
  PendingVerification: "Pending verification",
  Active: "Active",
  Suspended: "Suspended",
  Deactivated: "Deactivated",
};

const ONBOARDING_LABELS: Record<string, string> = {
  Registered: "Registered",
  ProfileCompleted: "Profile completed",
  KycSubmitted: "KYC submitted",
  KycVerified: "KYC verified",
  Completed: "Onboarding complete",
};

/** View/edit legal name, display name and email, plus a read-only status summary. */
export function ProfileDetailsSection() {
  const queryClient = useQueryClient();
  const [isEditing, setIsEditing] = useState(false);

  const query = useQuery({ queryKey: ["provider-profile"], queryFn: getProfile });

  const form = useForm<ProfileFormValues>({
    resolver: zodResolver(profileSchema),
    defaultValues: { legalName: "", displayName: "", email: "" },
  });

  // Populate the form once the profile loads (and whenever it refetches
  // while not mid-edit, so a save elsewhere stays reflected).
  useEffect(() => {
    if (query.data && !isEditing) {
      form.reset({
        legalName: query.data.legalName,
        displayName: query.data.displayName,
        email: query.data.email ?? "",
      });
    }
  }, [query.data, isEditing, form]);

  const mutation = useMutation({
    mutationFn: (values: ProfileFormValues) =>
      updateProfile({
        legalName: values.legalName,
        displayName: values.displayName,
        email: values.email === "" ? undefined : values.email,
      }),
    onSuccess: (profile) => {
      queryClient.setQueryData(["provider-profile"], profile);
      setIsEditing(false);
    },
  });

  const onSubmit = form.handleSubmit((values) => mutation.mutate(values));

  if (query.isPending) {
    return (
      <Card title="Profile">
        <p className="text-sm text-neutral-500">Loading profile…</p>
      </Card>
    );
  }

  if (query.isError) {
    return (
      <Card title="Profile">
        <Alert>{describeError(query.error)}</Alert>
      </Card>
    );
  }

  const profile = query.data;

  return (
    <Card title="Profile" description="Your legal identity and how you appear to customers.">
      <div className="mb-4 flex flex-wrap gap-4 text-sm">
        <span className="rounded-full bg-black/5 px-3 py-1 dark:bg-white/10">
          Status: {STATUS_LABELS[profile.status] ?? profile.status}
        </span>
        <span className="rounded-full bg-black/5 px-3 py-1 dark:bg-white/10">
          Onboarding: {ONBOARDING_LABELS[profile.onboardingStatus] ?? profile.onboardingStatus}
        </span>
        <span className="rounded-full bg-black/5 px-3 py-1 dark:bg-white/10">Phone: {profile.phone}</span>
      </div>

      {!isEditing ? (
        <div className="flex flex-col gap-2 text-sm">
          <p>
            <span className="font-medium">Legal name:</span> {profile.legalName}
          </p>
          <p>
            <span className="font-medium">Display name:</span> {profile.displayName}
          </p>
          <p>
            <span className="font-medium">Email:</span> {profile.email ?? "Not set"}
          </p>
          <div className="mt-2">
            <Button type="button" variant="secondary" onClick={() => setIsEditing(true)}>
              Edit profile
            </Button>
          </div>
        </div>
      ) : (
        <form onSubmit={onSubmit} className="flex flex-col gap-4" noValidate>
          {mutation.isError ? <Alert>{describeError(mutation.error)}</Alert> : null}
          <Field
            label="Legal name"
            error={form.formState.errors.legalName?.message}
            {...form.register("legalName")}
          />
          <Field
            label="Display name"
            error={form.formState.errors.displayName?.message}
            {...form.register("displayName")}
          />
          <Field
            label="Email (optional)"
            type="email"
            error={form.formState.errors.email?.message}
            {...form.register("email")}
          />
          <div className="flex gap-2">
            <Button type="submit" disabled={mutation.isPending}>
              {mutation.isPending ? "Saving…" : "Save changes"}
            </Button>
            <Button
              type="button"
              variant="secondary"
              onClick={() => {
                setIsEditing(false);
                form.reset({
                  legalName: profile.legalName,
                  displayName: profile.displayName,
                  email: profile.email ?? "",
                });
              }}
            >
              Cancel
            </Button>
          </div>
        </form>
      )}
    </Card>
  );
}
