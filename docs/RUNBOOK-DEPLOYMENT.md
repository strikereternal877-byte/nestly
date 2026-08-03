# RUNBOOK: Deploy, Rollback, Incident Response, On-Call (tasks 142a-142d)

Companion to `docs/DEVOPS.md` CI/CD PIPELINE and OBSERVABILITY sections, and
to `docs/RUNBOOK-BACKUP-RESTORE.md` (database-specific — that runbook is the
authority for backup/restore; this one covers everything else an on-call
engineer needs). Deployable units, environments, and the registry/host
OPEN DECISIONS are as documented in `docs/DEVOPS.md` — this runbook does not
duplicate them, only the operational steps that sit on top.

## 142a — Deploy

Three services deploy independently: `consumer-api`, `admin-api`,
`provider-api` (images built from `backend/*/Dockerfile`, pushed to GHCR per
`docs/DEVOPS.md` OPEN DECISIONS resolution in task 138a).

**Normal path — fully automatic, no manual steps:**

1. Merge a PR into `develop` → `.github/workflows/cd-staging.yml` runs the
   same build/test gate as `ci.yml`, builds and pushes the three images to
   `ghcr.io`, runs `database/scripts/apply-migrations.sh` against staging as
   its own job (always before deploy), then deploys via the `staging`
   GitHub Environment over SSH using `deploy/docker-compose.deploy.yml`.
2. Merge `develop` into `main` (or merge a PR targeting `main`) →
   `.github/workflows/cd-production.yml` runs the identical sequence
   against the `production` GitHub Environment. Production deploys pause
   for approval if **Settings → Environments → production → Required
   reviewers** is configured (documented in the workflow header as a
   repo-settings action — not expressible in YAML).

**Preconditions that make a deploy actually reach a host** (both workflows
fail fast and loudly, by design, until these exist — see `docs/DEVOPS.md`
OPEN DECISIONS 1-4): `STAGING_*` / `PRODUCTION_*` SSH host, user, and key
secrets, plus a provisioned Docker host running compose. There is no real
staging/production host today, so a deploy triggered now will pass build
and migration, then fail at the SSH step — check the Actions run log for
which secret is missing before assuming a code problem.

**Verifying a deploy landed:** hit the liveness (`/health/live`) and
readiness (`/health/ready`) endpoints each API exposes (wired via
`MapHealthChecks` in every `Program.cs` per `docs/DEVOPS.md` HEALTH CHECKS)
on all three services, then check `/metrics` (Prometheus scrape endpoint,
task 137) is serving the new build.

## 142b — Rollback

`.github/workflows/rollback.yml`, triggered manually
(`workflow_dispatch`) — never automatic, so a bad deploy never
self-heals into a second bad deploy.

Inputs: `environment` (`staging`/`production`), `image_tag` (the prior known
good tag from the GHCR package list), optional `target_migration`.

1. **App rollback (always the first move):** redeploys `image_tag` via the
   same SSH + `docker-compose.deploy.yml` path as a normal deploy — no
   rebuild, so it's fast and doesn't re-run CI. `environment:
   ${{ inputs.environment }}` on this job means a production rollback still
   requires the same reviewer approval as a forward deploy — an incident is
   not a reason to bypass the approval gate.
2. **Database rollback (opt-in, only if `target_migration` is set):** runs
   `dotnet ef database update <target_migration>`. This is only safe when
   every EF Core migration between the current and target migration has a
   lossless `Down()` — verify that before using it. **Default guidance:
   redeploy the previous app image and forward-fix, rather than rolling the
   database back** — a fabricated automatic DB-rollback guarantee would be
   worse than an honest manual check here.

## 142c — Incident response

**Detection.** Three sources, in order of how fast they surface a problem:

1. `FailureRateAlertMonitor` (task 137) — a rolling per-category failure-rate
   monitor wired into payment (`PaymentWebhookService`), booking
   (`BookingService.CreateAsync`, `BookingMetricsHandler`), and notification
   (`NotificationDispatchService`) paths. When a category's failure rate
   crosses its configured threshold in `MetricsOptions`/`"Metrics"`, it
   raises an error-level structured log event with a distinct `EventId` and
   an `AlertCode` property (`Payment.FailureRateAlert`,
   `Booking.FailureRateAlert`, `Booking.SlotCapacityReached`,
   `Notification.FailureRateAlert` per channel) — see
   `backend/shared/Infrastructure/Observability/MetricsAlertEvents.cs`.
   No external paging destination is wired yet (`docs/DEVOPS.md` OPEN
   DECISIONS — monitoring/alerting stack unresolved): today this means
   grepping/alerting on these `AlertCode`s in whatever log aggregator reads
   the structured logs, until a Slack/PagerDuty/email sink is chosen.
2. `/metrics` Prometheus scrape endpoint on each API — request rate,
   latency, error rate (counters/histograms on the `"Nestly"` Meter).
3. Health check failures (`/health/live`, `/health/ready`) surfaced by
   whatever orchestrator/load balancer is in front of the APIs once one
   exists.

**Response steps, in order:**

1. **Triage severity.** Payment and booking failures are availability-priority
   per `docs/DEVOPS.md` SCALABILITY AND AVAILABILITY (SRS §29.4) — treat
   `Payment.FailureRateAlert` and `Booking.FailureRateAlert` as the highest
   urgency; a single-channel `Notification.FailureRateAlert` (e.g. SMS
   provider down) is lower urgency since booking/payment still succeed.
2. **Confirm scope.** Check whether the failure is isolated to one API
   (consumer/admin/provider) or systemic (e.g. shared Postgres/Redis down) —
   the three APIs deploy and scale independently, so a bad `consumer-api`
   deploy does not imply `admin-api`/`provider-api` are affected.
3. **Mitigate.** If the alert correlates with a recent deploy, roll back
   (142b) first — restoring service takes priority over root-causing during
   an active incident. If it does not correlate with a deploy (e.g.
   downstream payment gateway outage), there is no code-side rollback to
   perform; monitor the failure-rate metric for recovery and communicate
   status.
4. **Idempotency is the safety net, not a substitute for care.** Payment
   webhook processing and booking creation are both idempotent by design
   (`docs/DEVOPS.md` GRACEFUL SHUTDOWN; task 69's signed-callback + dedup
   handling; task 135b's `TryAddAsync`/unique-index dedup fallback) — retried
   in-flight work during a redeploy or restart will not double-charge or
   double-book, but this does not mean skipping the triage/mitigate steps.
5. **Verify recovery.** Confirm the triggering `AlertCode`'s failure rate has
   dropped back under threshold and the relevant health checks are green
   before closing the incident.
6. **Write it up.** Record what happened, what the log evidence was
   (`AlertCode` + timestamps), what mitigated it, and any follow-up task —
   add follow-up work to `tasks.csv` under the relevant phase rather than a
   separate incident tracker, since that CSV is this repo's single backlog.

## 142d — On-call basics

- **Scope of on-call today:** this repo has CI/CD, metrics, and structured
  alert logging (Phase 8), but no real staging/production host and no
  external paging integration yet (`docs/DEVOPS.md` OPEN DECISIONS). On-call
  currently means: someone is watching the structured logs / `/metrics`
  endpoints and knows this runbook — not "someone gets paged at 3am,"
  since there is nowhere for a page to come from yet. Update this section
  once a paging destination is decided.
- **What to have open:** `/metrics` for all three APIs, log aggregator
  filtered to `AlertCode` on the `Nestly` structured-log source, and this
  runbook plus `docs/RUNBOOK-BACKUP-RESTORE.md`.
- **Escalation path for a database-affecting incident:** stop, do not
  improvise a manual `psql` fix under pressure — use
  `docs/RUNBOOK-BACKUP-RESTORE.md`'s tested restore procedure
  (`database/scripts/backup-postgres.sh` /
  `database/scripts/restore-postgres.sh`), which has been drilled end-to-end
  with row-count and checksum verification.
- **Permissions an on-call engineer needs:** access to trigger
  `rollback.yml` (`workflow_dispatch` on this repo), the `staging`/
  `production` GitHub Environments (for approval gates), and read access to
  wherever structured logs land.
- **What on-call is not:** a substitute for fixing the root cause during
  business hours — mitigation (rollback, forward-fix) restores service;
  the incident write-up (142c step 6) is what turns into a real fix.
