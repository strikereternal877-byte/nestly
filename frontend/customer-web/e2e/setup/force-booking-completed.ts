import { execFileSync } from "node:child_process";

/**
 * Test-only shortcut: forces a booking straight to BookingStatus.Completed
 * so the review-submission spec (140d) can exercise the review API/UI
 * without also standing up the provider-api + provider KYC + assignment +
 * job-completion chain (ProviderJobService.CompleteAsync is the only real
 * path to Completed - backend/shared/Infrastructure/Services/ProviderJobService.cs:169).
 * That fulfilment chain has its own coverage (Phase 7's provider tests); this
 * suite's job is the customer-facing review flow, not proving how a booking
 * gets fulfilled. Matches BookingConfiguration's `HasConversion<string>()`
 * (stores the bare enum member name) and inserts a matching
 * booking_status_history row so ReviewService's submission-window check
 * (which reads booking.StatusHistory) sees a real completion timestamp.
 */
export function forceBookingCompleted(bookingId: string): void {
  const sql = `
    UPDATE booking SET status = 'Completed' WHERE id = '${bookingId}';
    INSERT INTO booking_status_history (id, booking_id, from_status, to_status, reason, changed_at_utc)
    VALUES (gen_random_uuid(), '${bookingId}', 'Confirmed', 'Completed', 'E2E test setup: forced completion for review-flow test.', now());
  `;
  execFileSync("docker", ["exec", "-i", "nestly-postgres-1", "psql", "-U", "nestly", "-d", "nestly", "-v", "ON_ERROR_STOP=1"], {
    input: sql,
    stdio: ["pipe", "pipe", "inherit"],
  });
}
