# DESIGN.md — The Service Ledger

Visual system for `customer-web`'s marketing surfaces (homepage, category
browse, service detail). Admin-web and provider-web are separate Next.js
apps with their own copy of the token files and are not covered here.

```
THESIS: The service ledger you'd actually keep — not another gradient hero.
OWN-WORLD: kraft/manila ground, ink-blue text, stamped rust-red confirmation ink, per-category tab colors like binder dividers, typewriter/stamp display type + grotesk body + mono ledger numerals.
STORY: visitor sees their home's service record already exists — pick a tab (category), see upfront-priced ledger rows, book a slot, recurring visits stamp the record automatically.
FIRST VIEWPOINT: full-bleed kraft ledger card floating on dark ink ground, category tabs across the top edge like real dividers, one open "page" showing a real booking row mid-motion (a stamp landing), primary CTA as a red ink-stamp button.
FORM: Service Ledger, candidate 7/7, seed key 394fa208.
```

This is candidate 7 of a 7-candidate grounded roll (seed `394fa208`),
confirmed by the user over two challenger alternates and a standing
"conventional SaaS" exit. It replaces the prior "Resido real-estate
reference" navy palette — not a fixed brand commitment, per `PRODUCT.md`.

## Palette

Everything below lives as CSS variables in `src/app/globals.css`, wrapped by
`tailwind.config.ts` — no component hardcodes a hex value.

### Light — "kraft ledger"

| Role | Token | Hex | Used for |
|---|---|---|---|
| Ground | `--bg` | `#D9C39A` | Page background — genuine kraft/manila, not a cream pastel |
| Surface | `--surface` | `#EDE0C4` | Cards, the ledger "page" |
| Surface 2 | `--surface-2` | `#E3D2AE` | Nested rows, banded sections |
| Surface 3 | `--surface-3` | `#D6C298` | Deepest nested surface |
| Border | `--border` | `#B89968` | Aged-kraft hairlines, ruled lines |
| Border strong | `--border-strong` | `#9B7C4E` | Card edges, tab dividers |
| Ink (text) | `--fg` | `#1B2A4A` | Primary text — deep ink-blue, not black |
| Ink muted | `--fg-muted` | `#374462` | Secondary text (AA-adjusted, see below) |
| Ink subtle | `--fg-subtle` | `#3C4A68` | Meta/hint text (AA-adjusted) |
| **Stamp red (brand-600)** | `--brand-600` | `#C1442D` | Primary CTAs, focus ring, active tab underline, price highlights, brand chips — used at real weight, not reserved for links |
| Accent (ochre) | `--accent-500` | `#B07A1C` | Ratings, Nestly Coins, "highlighted value" — kept distinct from stamp red |

Category tab colours (binder dividers, one per real category in
`public/images/categories`, mapped by slug in `src/lib/categoryVisuals.ts`):

| Category | Hex |
|---|---|
| Home cleaning | `#4F7A6B` sage-teal |
| AC repair | `#51606E` slate |
| Appliance repair | `#6B7480` slate (lighter) |
| Carpentry | `#8B6B2E` ochre |
| Electrical | `#B3860B` amber |
| Painting | `#6E5A73` violet-gray |
| Pest control | `#6B7A3A` olive |
| Plumbing | `#3E6485` steel-blue |
| Salon for men | `#8C5A63` mauve |
| Salon for women | `#A85A6E` rose |

### Dark — "night ledger"

| Role | Token | Hex |
|---|---|---|
| Ground | `--bg` | `#211C16` — warm-dark charcoal, not neutral/pure black |
| Surface | `--surface` | `#2A241B` |
| Ink (text) | `--fg` | `#F0E6D2` — pale warm off-white |
| Stamp red (brand-600) | `--brand-600` | `#B05438` — brightened/desaturated from light mode for contrast on dark, AA-adjusted (see below) |

Category tab colours brighten/desaturate the same way (e.g. plumbing
`#82AACE`, carpentry `#C4A260`) — same mapping, dark-mode CSS override.

Status colours (success/warning/danger/info) stay **distinct roles** from
the stamp red even though danger sits in the same warm family — different
hues, never a repurposed accent, in both themes.

### AA-contrast adjustments made during the build

The initial hand-picked values from the brief failed WCAG AA (4.5:1) in a
few places; all were darkened/adjusted until they passed, verified with a
standard relative-luminance contrast calculation:

- Light `--fg-muted`: `(71,84,115)` → `(55,68,98)` — was 4.39:1 on `--bg`.
- Light `--fg-subtle`: `(82,96,128)` → `(60,74,104)` — was 3.66:1 on `--bg`.
- Light `--warning`: `(146,98,20)` → `(120,80,14)` — was 3.70:1 on `--warning-soft`.
- Light `--success`: `(33,110,74)` → `(26,92,60)` — was 4.43:1 on `--success-soft`.
- Dark `--brand-600` (and the ramp around it): `(214,110,76)` → `(176,84,56)` —
  white button-label text was 3.39:1 on the original, under the 4.5:1 floor;
  the darkened value clears 5.03:1.

All pairs above (and every `fg*` on `bg`/`surface`/`surface-2`, `white` on
`brand-600`, and each status tone on its `-soft` background, in both themes)
were re-verified after the fix.

## Type

| Role | Face | Loaded as |
|---|---|---|
| Display / stamp | **Special Elite** (Google Font) | `next/font/google`, new `--font-display` var, `font-display` Tailwind key. Headlines, category tab labels, stamped callouts only — never body copy. |
| Body / UI | **Archivo** (Google Font) | Reuses the existing `--font-geist-sans` var/`font-sans` key, so every existing call site repaints without a second change. |
| Numerals / dates / prices | **Geist Mono** (unchanged, local font) | `--font-geist-mono` / `font-mono`, paired with the existing `.nums` tabular-figure utility for ledger-row numerals — prices, durations, booking counts, service counts. |

## Component language

- **Category tiles** (`CategoryTile.tsx`) are literal binder-divider tabs: a
  coloured tab flap (clip-path notch, one hue per category) sits above a
  kraft page body with the category icon, a decorative rating stamp, and a
  bordered "Explore" mark — not a photo card, not a pill.
- **Hero** (`HeroBanner.tsx`): a dark ink ground (`--ink-ground`, fixed,
  theme-independent — the hero's own staged backdrop, same role
  `--brand-950` played before) with a floating kraft ledger card. Category
  tabs run across the card's top edge like real dividers; the body shows the
  functional `SearchBar` plus one sample ledger row whose "Confirmed" stamp
  animates in on load.
- **Service detail**: reads as one open ledger page — category name and
  title in the display face, `PriceCalculator`'s total line gets a
  bordered/tinted "stamped" treatment with mono numerals, and the primary
  action is `StampButton` (new, `src/components/StampButton.tsx`).
- **StampButton**: a red ink-stamp CTA. `whileTap` scales down and rotates
  slightly before springing back — the one deliberate "stamp landing"
  micro-interaction, not spammed across every control. Built on `motion/react`
  (the existing `motion` dependency).
- **Ruled lines** (`.ruled-lines` utility in `globals.css`): a cheap
  repeating-linear-gradient standing in for a hairline per ledger row,
  used on category tiles and available for any future ledger-row list.

## Motion

- Reduced motion is handled once, product-wide, via `MotionConfig
  reducedMotion="user"` in `app/providers.tsx` (pre-existing) — every
  `motion.*` gesture in this build, including `StampButton`'s stamp landing
  and the hero's sample-row stamp, inherits that guard automatically; no
  component here re-implements a `prefers-reduced-motion` check.
- One spring feel for tactile press interactions (`SPRING` in
  `components/motion.tsx`, pre-existing), plus a snappier dedicated spring
  for the stamp impact (`StampButton`'s `STAMP_SPRING`) — quick and
  purposeful, not decorative bounce.

## Dark mode

Class-driven (`.dark` on `<html>`), pre-paint script unchanged
(`src/lib/theme.ts` → `THEME_INIT_SCRIPT` in `layout.tsx`). Every token above
has a light and dark value; nothing in the new marketing components branches
on `dark:` directly except where Tailwind's `dark:` variant is the
established idiom already in use elsewhere in the codebase (badge tints,
etc.) — the palette shift itself is carried entirely by the token layer.

## Deferred / out of scope

- `components/ui.tsx` (`Button`, `Card`, `Modal`, …) was **not** restructured
  — it is byte-identical across `customer-web`/`admin-web`/`provider-web` by
  convention, and those two apps are explicitly out of scope. It picks up
  the new palette automatically (no hardcoded hex), but does not carry the
  ledger-tab/stamp component language itself; `StampButton` and
  `CategoryTile`'s tab treatment live outside it instead.
- `ServiceAvailability.tsx`, `ServiceFaqs.tsx`, `ReviewsSummary.tsx` (service
  detail page) and `SiteHeader.tsx` (global chrome) were retthemed by the
  token layer only — not given bespoke ledger-row/tab treatments — to keep
  this pass inside its time box.
- `ServiceCard.tsx` is shared with `/search` (not in the named scope); its
  retheme + mono-price/border tweaks apply there too as a side effect of
  being a shared component.
