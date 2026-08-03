# Phase 12 — Premium UI & UX Overhaul: handoff

Working branch: **`phase-12-premium-ui`** (branched from `main` at `cdb1d0b`).

The authoritative backlog is **`tasks.csv` rows 209–229**, phase
`Phase 12 - Premium UI & UX Overhaul`. This file is orientation only — when the
two disagree, `tasks.csv` wins.

Per `AGENTS.md`: a row's `status` is not evidence. Before building on any row
marked `done`, grep for the artifact it claims. No row may be marked `done`
without a real passing `npm run build` (all three frontends are Next projects;
`dotnet build` is irrelevant to this phase).

---

## State as of 2026-08-02

| Row | Title | Status |
|---|---|---|
| 209 | Token foundation | **done** |
| 210 | Component kit (`ui.tsx`) | **done** |
| 211 | Dark mode + theme toggle | **done** |
| 212 | Customer app shell | **done** |
| 213 | Admin app shell | **done** |
| 214 | Provider app shell | **done** |
| 216 | Customer home & discovery | **done** |
| 215 | Auth screens | **partial** — customer-web only, see below |
| 217 | Service detail & slots | **partial** — see below |
| 228 | Bug sweep | **partial** — one defect class closed, see below |
| 218–227, 229 | — | **todo** |

Commits: `54ec781` (209–211), `4c8b6f6` (212 + partial 216), `b8a57c8` (216
finished), `aff73e9` (213–214), `45c8a15` (215 partial), `e8e2ae7` (217
partial), `28c8eaa` (228 partial — date fix).

### 217 is partial

Done: `SlotPicker`, `app/services/[slug]/page.tsx`, `ServiceFaqs`,
`ReviewsSummary`.

**Still on old styling:** `PriceCalculator`, and `ServiceAvailability`'s markup
(only its date helper was corrected).

### 228 — use `lib/date.ts`, never `toISOString` for dates

One systemic class is closed. 14 sites built a `YYYY-MM-DD` calendar date with
`new Date().toISOString().slice(0, 10)`, which converts to UTC first — in IST
that returns *yesterday* before 05:30 local. It had shipped into the slot
strip, booking summary, reschedule and recurring-booking date defaults.

`lib/date.ts` (`toLocalIsoDate` / `todayIsoDate` / `isoDateOffsetFromToday`) is
now in all three apps. **Use it for anything calendar-shaped.** A grep for
`toISOString()` in the `src` trees should return nothing but that file's own
doc comment — if it returns more, the bug is back.

The rest of row 228 is untouched: unhandled rejections, missing error
boundaries, stale cache after mutation, rapid-navigation races, hydration
warnings, the dead admin nav links, forms losing data on failure, and
double-submit on slow networks. That part must be evidence-based against
running apps, not a code read.

**All shells and the whole design foundation are now in place.** What remains
is screen-level work inside each app, plus the four cross-cutting passes.

### 215 is partial — customer-web only

Done: `customer-web`'s `/login`, `/register`, `/forgot-password`, plus the new
shared `components/auth-ui.tsx` (`AuthShell`, `Segmented`, `OtpField`,
`useResendCountdown`, `ResendRow`).

**Still on old styling:** `admin-web/src/app/login/page.tsx`,
`provider-web/src/app/login/page.tsx`, `provider-web/src/app/register/page.tsx`.
Both apps' own `/login` pages are deliberately kept (row 206) for direct or
bookmarked origin access — restyle them, don't delete them.

`auth-ui.tsx` currently lives only in customer-web. Sessions B and C should
copy it into their app rather than importing across app boundaries.

---

## The foundation you are building on

**Design tokens** live in each app's `src/app/globals.css`. Components must not
contain a hex value or a raw `neutral-*` / `black/10` class — everything
resolves through tokens mapped in `tailwind.config.ts`:

- Colour: `brand-50..950` (violet-indigo, `600` = primary), `accent-*` (amber,
  reserved for coins/rewards/ratings), `bg`, `surface`/`surface-2`/`surface-3`,
  `fg`/`fg-muted`/`fg-subtle`/`fg-on-brand`, `line`/`line-strong`, and
  `success`/`warning`/`danger`/`info` each with a `-soft` companion.
- Type: `text-display-sm|md|lg|xl` carry their own tracking — do not add
  `tracking-*` at the call site.
- Elevation: `shadow-xs|sm|md|lg|xl|brand`. Radius: `rounded-lg|xl|2xl|3xl`.
- Motion: `duration-fast|DEFAULT|slow` with `ease-out`, and the
  `animate-fade-in|rise|pop|shimmer` keyframes. All gated on
  `prefers-reduced-motion` in `globals.css`.
- `.nums` applies tabular figures — use on any column of amounts.

**Brand hue is a single point of change.** Nothing downstream references the
hue, so re-theming is editing the `--brand-*` ramp in `globals.css`. This
choice was made without brand guidance existing anywhere in the repo — treat it
as provisional until the product owner confirms it.

**Dark mode is class-driven** (`darkMode: "class"`), not media-driven.
`lib/theme.ts` owns the preference; `THEME_INIT_SCRIPT` runs blocking in
`<head>` in every root layout so there is no light-theme flash. `<html>` carries
`suppressHydrationWarning` because that script mutates it pre-hydration. Any new
chrome should mount `ThemeToggle`, which deliberately renders a fixed-size
placeholder until mounted so it cannot cause a hydration mismatch.

**`components/ui.tsx` is byte-identical across all three apps** (verify with
`md5`). It is a deliberate superset — an app that never renders a `Table` still
ships the identical file. Exports: `cx`, `Card`, `Divider`, `Alert`, `Badge`,
`Spinner`, `Skeleton`, `SkeletonText`, `EmptyState`, `Field`, `Textarea`,
`Select`, `Checkbox`, `CheckboxField`, `Button`, `IconButton`, `PageHeading`,
`StatTile`, `Table`/`THead`/`TH`/`TBody`/`TR`/`TD`/`TableMessage`, `Tabs`,
`Modal`, `ToastProvider`/`useToast`.

> **If you change `ui.tsx`, you must copy it to the other two apps in the same
> commit.** There is no shared package. A one-app edit silently drifts the
> three. `Field`'s leading adornment prop is named `leading`, not `prefix` —
> `prefix` is a real HTML attribute typed `string` and collides.

**Watch the e2e selectors.** `customer-web/e2e/` addresses UI by accessible
name, so restyling can break tests without touching a test file. Names that
must survive: headings `"Popular categories"`, `"All categories"`,
`"Review your booking"`, `"Booking placed!"`, `"Cancel booking"`,
`"Reschedule booking"`, `"Leave a review"`, `"Booking cancelled"`; buttons
`"Proceed to book"`, `"Confirm cancellation"`, `"Confirm reschedule"`,
`"Submit review"`; links `"Book now"`, `"View booking details"`; and
`role="tablist"` named `"Booking status"` with a `"Upcoming"` tab. `Tabs` now
takes a `label` prop for exactly that last one — pass it in row 219.

---

## Running parallel sessions

The three apps are independent Next projects with no cross-imports, so these
file trees never touch and can be worked simultaneously:

| Session | Scope | Rows |
|---|---|---|
| A | `frontend/customer-web/` | 217, 218, 219, 220 |
| B | `frontend/admin-web/` | 221, 222 |
| C | `frontend/provider-web/` | 223 |

Row 215 (auth screens) spans all three — split it, each session does its own
app's login/register/OTP/forgot-password. Preserve row 206's unified-login mode
selector and the fragment-based handoff to `/auth/callback` exactly.

**Coordination rules**

1. `ui.tsx`, `globals.css`, `tailwind.config.ts` are **frozen** for sessions B
   and C. Need a new primitive? Put it in a separate file in your own app and
   flag it for porting at the end.
2. `tasks.csv` is the only shared file. Edit only your own rows; conflicts stay
   line-level.

**Must run last, after all screens exist:** 225 (a11y), 226 (responsive), 227
(motion), 228 (bug sweep). **229 (sign-off) cannot be parallelized** — it gates
on everything else.

---

## Definition of done for any Phase 12 row

1. `npx tsc --noEmit` clean in the affected app(s).
2. `npm run build` exit 0 — this also runs lint, and lint failures fail the
   build (an unused import already broke it once in this phase).
3. Only then set `status` to `done` in `tasks.csv`, and write a note recording
   what was verified and **what was deliberately left out**.
4. Commit referencing the row id.

If a row is only partly finished, leave `status` as `todo` and write
`PARTIALLY DONE ... do not treat as complete` in the note with the exact
remaining file list. Row 216's note is the worked example.
