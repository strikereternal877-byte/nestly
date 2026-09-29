"use client";

import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Alert, Button, Modal, Select, Textarea, useToast } from "@/components/ui";
import { describeError } from "@/lib/api";
import {
  ADMIN_ATTENDANCE_ACTIONS,
  ATTENDANCE_LABELS,
  MonthlyServiceAttendanceStatus,
  correctMonthlyAttendance,
  formatDay,
  resolveMonthlyDispute,
} from "../_lib/monthly-service-api";
import type { MonthlyServiceAttendanceItem } from "../_lib/monthly-service-api";

const RECORDED_STATUSES = [
  MonthlyServiceAttendanceStatus.Present,
  MonthlyServiceAttendanceStatus.CustomerUnavailable,
  MonthlyServiceAttendanceStatus.CustomerSkipped,
  MonthlyServiceAttendanceStatus.ProviderLeave,
  MonthlyServiceAttendanceStatus.Absent,
];

/** Statuses a day can be corrected to when a customer's dispute is upheld - never a charged one. */
const UNCHARGED_STATUSES = [
  MonthlyServiceAttendanceStatus.Absent,
  MonthlyServiceAttendanceStatus.ProviderLeave,
  MonthlyServiceAttendanceStatus.CustomerSkipped,
];

/**
 * Admin attendance action for one day (docs/MONTHLY-SERVICE.md ATTENDANCE
 * SYSTEM): resolve an open customer dispute (uphold with a corrected,
 * uncharged status, or reject), or correct a day nobody recorded properly.
 * Both are refused server-side once the day's month has been invoiced.
 */
export function AttendanceActionModal({
  item,
  onClose,
  invalidateKeys,
}: {
  item: MonthlyServiceAttendanceItem | null;
  onClose: () => void;
  invalidateKeys: readonly (readonly unknown[])[];
}) {
  const toast = useToast();
  const queryClient = useQueryClient();
  const isDispute = item?.allowedActions.includes(ADMIN_ATTENDANCE_ACTIONS.resolveDispute) ?? false;
  const [decision, setDecision] = useState<"uphold" | "reject">("uphold");
  const [status, setStatus] = useState(MonthlyServiceAttendanceStatus.Absent);
  const [note, setNote] = useState("");

  useEffect(() => {
    if (!item) return;
    setDecision("uphold");
    setStatus(
      item.allowedActions.includes(ADMIN_ATTENDANCE_ACTIONS.resolveDispute) || item.status === MonthlyServiceAttendanceStatus.Scheduled
        ? MonthlyServiceAttendanceStatus.Absent
        : item.status,
    );
    setNote("");
  }, [item]);

  const mutation = useMutation({
    mutationFn: () => {
      if (!item) throw new Error("No day selected.");
      return isDispute
        ? resolveMonthlyDispute(item.id, { upheld: decision === "uphold", correctedStatus: decision === "uphold" ? status : null, note: note.trim() || null })
        : correctMonthlyAttendance(item.id, status, note.trim() || null);
    },
    onSuccess: (updated) => {
      toast("success", `${formatDay(updated.date)} is now ${ATTENDANCE_LABELS[updated.status]}.`);
      invalidateKeys.forEach((key) => void queryClient.invalidateQueries({ queryKey: key }));
      onClose();
    },
  });

  const options = (isDispute ? UNCHARGED_STATUSES : RECORDED_STATUSES).map((s) => ({ value: String(s), label: ATTENDANCE_LABELS[s] }));

  return (
    <Modal
      open={item !== null}
      onClose={() => !mutation.isPending && onClose()}
      title={item ? `${isDispute ? "Resolve dispute" : "Correct attendance"} · ${formatDay(item.date)}` : ""}
      description={
        item
          ? isDispute
            ? `Recorded as ${ATTENDANCE_LABELS[item.status]}. Customer says: “${item.disputeReason ?? ""}”`
            : `Currently ${ATTENDANCE_LABELS[item.status]}.`
          : undefined
      }
      size="sm"
      footer={
        <>
          <Button variant="secondary" disabled={mutation.isPending} onClick={onClose}>
            Cancel
          </Button>
          <Button loading={mutation.isPending} onClick={() => mutation.mutate()}>
            Save
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {isDispute ? (
          <Select
            label="Decision"
            value={decision}
            onChange={(e) => setDecision(e.target.value as "uphold" | "reject")}
            options={[
              { value: "uphold", label: "Uphold — customer is right, don't charge" },
              { value: "reject", label: "Reject — keep as recorded" },
            ]}
          />
        ) : null}
        {!isDispute || decision === "uphold" ? (
          <Select
            label={isDispute ? "Correct the day to" : "What actually happened"}
            value={String(status)}
            onChange={(e) => setStatus(Number(e.target.value) as MonthlyServiceAttendanceStatus)}
            options={options}
          />
        ) : null}
        <Textarea label="Note (optional)" maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
        {mutation.isError ? <Alert tone="error">{describeError(mutation.error)}</Alert> : null}
      </div>
    </Modal>
  );
}
