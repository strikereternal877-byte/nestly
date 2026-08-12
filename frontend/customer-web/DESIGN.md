# Nestly customer-web — "Quiet Ground" visual direction

Second, alternate redesign of the customer-web marketing surfaces (homepage,
category browse, category detail, service detail). Kept as its own branch
alongside "The Service Ledger" (kraft/stamp/typewriter) — this is a different
world, not a revision of it, and shares none of that direction's visual
language.

## Brief

The user compared Airbnb, Linear, and Aesop live in-browser and chose:
**"Airbnb & Aesop are very good. Use premium side from both."**

- **From Airbnb:** warm off-white ground, generously rounded cards/inputs,
  big real photography in soft-cornered cards, a friendly confident sans, one
  warm accent used sparingly, lots of breathing room.
- **From Aesop:** full-bleed editorial photography as the dominant visual
  element, a quiet restrained serif for headlines, muted warm-neutral
  palette, understated hierarchy over loud color, considered restraint.

Synthesis: **quiet, warm, photography-led premium.**

## Palette

Everything resolves through CSS variables in `src/app/globals.css`, consumed
via `tailwind.config.ts`'s `rgb(var(--token) / <alpha-value>)` wrapper —
components never hardcode a hex value.

### Light

| Token | Hex | Role |
|---|---|---|
| `--bg` | `#F7F3EC` | Page ground — warm ivory, never stark white |
| `--surface` | `#FDFBF7` | Card/panel fill — a whisper warmer than bg |
| `--surface-2` | `#F1ECE1` | Deeper stone panel (full-bleed section bands) |
| `--surface-3` | `#E8E1D2` | Deepest neutral (image placeholders, skeletons) |
| `--fg` | `#2B2822` | Body text — warm charcoal, never pure black |
| `--fg-muted` | `#6B6354` | Secondary text (5.37:1 on bg) |
| `--fg-subtle` | `#746C5C` | Tertiary/meta text (4.70:1 on bg) |
| `--border` | `#E4DDCE` | Hairlines |
| `--brand-600` | `#96472A` | **The one accent** — primary CTAs, links, focus ring |
| `--accent-500` | `#B28230` | Secondary muted gold — rewards/ratings only |
| `--success` / `--warning` / `--danger` / `--info` | warm-retinted | Status only |

### Dark

| Token | Hex | Role |
|---|---|---|
| `--bg` | `#1C1A16` | Warm near-black, never pure black |
| `--surface` | `#24211B` | Card/panel fill |
| `--surface-2` | `#2C2822` | Deeper panel |
| `--fg` | `#F3EEE4` | Warm off-white body text |
| `--fg-muted` | `#B5AB97` | Secondary text (7.64:1 on bg) |
| `--fg-subtle` | `#A09682` | Tertiary text (5.94:1 on bg) |
| `--brand-600` | `#96472A` | Same accent hue as light — buttons stay white-on-brand at 6.5:1 in both modes |
| `--ring` | `#C3765A` (brand-400) | Focus ring, brightened for dark-bg legibility |

The `--brand-*` and `--accent-*` ramps are theme-invariant (defined once in
`:root`, not overridden in `.dark`) — only the semantic surface/text tokens
flip. This is the same architecture the codebase already used; only the hues
changed.

**Restrained color strategy:** one accent (brand/terracotta) carries every
primary CTA, focus ring, and interactive highlight across the whole product.
A second muted-gold ramp (`--accent-*`) is kept only because it already backs
an unrelated semantic surface (rewards/Nestly Coins/ratings elsewhere in the
app, out of this redesign's scope) — it is never used for navigation or
primary actions, so the "one accent for CTAs" rule holds where it matters.

## Type

| Role | Face | CSS var | Tailwind class |
|---|---|---|---|
| Body / UI | Public Sans | `--font-geist-sans` (kept for compatibility) | `font-sans` (default) |
| Headlines / display | Libre Caslon Text | `--font-display` | `font-display` / `font-serif` |
| Numerals (prices, tabular) | Geist Mono (unchanged) | `--font-geist-mono` | `font-mono` |

Public Sans replaces Jost as the body/UI face — Airbnb's warm-friendly
register. Libre Caslon Text is new: a quiet, non-decorative serif for page
and section headlines — Aesop's register. Neither is on the project's banned
list (Fraunces, Playfair, Cormorant, Lora, Crimson, Newsreader, Syne, Space
Grotesk/Mono, IBM Plex, Inter-as-display, DM Sans/Serif, Outfit, Plus Jakarta
Sans, Instrument Sans).

Both fonts are loaded via `next/font/google` in `src/app/layout.tsx`, kept
under the *existing* variable names where the rest of the app already
depends on them (`--font-geist-sans` → `font-sans`), plus one new variable
(`--font-display`) for the new serif role, wired into
`tailwind.config.ts`'s `fontFamily.display` / `fontFamily.serif`.

`h1`/section headings on the four marketing surfaces use `font-display`.
Shared, out-of-scope components (e.g. `PageHeading`, used by non-marketing
pages too) were left on the body sans deliberately — retheming them was out
of this redesign's stated scope.

## Photography & card language

- **Category browse** (`/categories`, home's category band): an editorial
  photography grid — `aspect-[4/5]` rounded photo cards with the category
  name as a quiet caption underneath, not an icon tile or a stat-badge card.
  The previous "Resido" pass's decorative deterministic rating/booking-count
  chrome was removed rather than carried forward — Nestly has no real
  per-category rating data, and this direction's restraint means not
  inventing one.
- **Category detail banner**: full-bleed real category photo
  (`category.bannerUrl`, the same field the tile already renders) with a
  bottom scrim (`.photo-scrim` utility) for legible white text. No photo yet
  for a category → falls back to a plain warm stone panel with dark text,
  not a fabricated image.
- **Service detail**: the photo (`service.coverImageUrl`) leads the page,
  full-bleed, above the price/booking column — the breadcrumb and title sit
  over it on the same scrim. No photo → the existing icon-on-gradient
  fallback from `lib/serviceVisuals.tsx`, unchanged.
- **Homepage hero**: photography-led, not a gradient mesh. It reuses the
  first available `category.bannerUrl` from the customer's city (same React
  Query cache key `CategoryTiles` already populates, so this costs no extra
  request) as a full-bleed backdrop with the scrim. No city selected yet, or
  no category has a photo → a plain warm surface panel, honestly, rather than
  a decorative gradient standing in for a photo that doesn't exist.
- **Cards** (`ServiceCard`, `CategoryTile`): borderless, shadow-only —
  `rounded-2xl` (now 1.75rem, see radii below), soft `shadow-xs` resting
  state, `shadow-lg`/`shadow-md` on hover, no colored border.

**Deferred / gap, not fabricated:** there is no admin-configurable
homepage-banner entity in the backend (documented pre-existing gap, carried
forward), so the hero's copy stays static; and a category/service with no
admin-uploaded photo yet genuinely has no photography to lead with — the
fallback states above are the honest answer, not an invented stock image.

## Radii

Bumped up from the previous scale as a deliberate part of this direction
(Airbnb-generous rounding), in `tailwind.config.ts`:

| Token | Before | Now |
|---|---|---|
| `DEFAULT` | 0.5rem | 0.75rem |
| `lg` | 0.75rem | 1.25rem |
| `xl` | 1rem | 1.5rem |
| `2xl` | 1.25rem | 1.75rem |
| `3xl` | 1.5rem | 2rem |

This is global (every `rounded-*` call site in the app inherits it), matching
the CSS-variable-driven token architecture — components never hardcode a
radius.

## Motion

Unchanged primitives, reused as-is (`src/components/motion.tsx`,
`globals.css`'s `nestly-rise`/`nestly-fade-in` keyframes):

- Card/tile hover: `translateY(-6px)` + shadow escalation via the shared
  `SPRING` (`stiffness: 420, damping: 32`) — a lift, never a scale-bounce.
- Entrance: staggered mount fade/rise (`Reveal`/`revealItem`), gated
  product-wide by `MotionConfig reducedMotion="user"` in `app/providers.tsx`
  plus the CSS `prefers-reduced-motion` block in `globals.css` that collapses
  all animation/transition durations to near-zero.
- Hero text: CSS-keyframe `animate-rise` (not the `motion` library) so the
  primary conversion path never depends on JS hydration.

No new motion primitives were introduced — the existing restrained-motion
system already matched this direction's brief.

## Dark mode

Class-driven via `.dark` on `<html>`, set by the pre-paint script in
`src/lib/theme.ts` (`THEME_INIT_SCRIPT`, unchanged mechanism). Warm
near-black ground (`#1C1A16`), warm off-white text, same terracotta accent
(buttons stay legible at 6.5:1 white-on-brand in both modes since the brand
ramp doesn't change between themes — only surfaces/text do).

## Accessibility — contrast checks performed

Every new text/background pairing below was checked against WCAG 2.1 AA
(4.5:1 for body text) with a relative-luminance script, not eyeballed:

| Pair | Light | Dark |
|---|---|---|
| `fg` / `bg` | 13.28:1 | 15.03:1 |
| `fg-muted` / `bg` | 5.37:1 | 7.64:1 |
| `fg-muted` / `surface` | 5.74:1 | 7.06:1 |
| `fg-subtle` / `bg` | 4.70:1 | 5.94:1 |
| `fg-subtle` / `surface` | 5.02:1 | 5.49:1 |
| white / `brand-600` (filled buttons) | 6.50:1 | 6.50:1 |
| `brand-700` / `surface` (light-mode brand-toned links) | 8.80:1 | n/a |

One adjustment made during this pass: the first `fg-subtle` value tried
(`#5C5547`) was *darker* than `fg-muted`, inverting the intended hierarchy
(subtle text read as higher-contrast than muted text). Lightened to
`#746C5C` so the ordering is correct — `fg-subtle` (4.70:1) reads lower
contrast than `fg-muted` (5.37:1) — while both still clear the 4.5:1 floor.

**Known limitation, unchanged from the prior direction:** `--brand-600` is
theme-invariant, so a small number of *decorative* hover-accent text colors
(e.g. `ServiceCard`'s "Explore details" link) use `dark:text-brand-400`
overrides to stay legible on dark surfaces rather than relying on the base
`brand-600`/`brand-700`, which fails AA as plain text on a dark background
(2.67:1) — verified and worked around at each call site introduced by this
redesign; pre-existing call sites elsewhere in the app were not audited
(out of scope).

Also preserved: single `:focus-visible` treatment app-wide, skip-to-content
link, semantic headings/landmarks on all four pages, `alt=""` + `aria-hidden`
on all decorative photography (category/service names are already rendered
as real text alongside every image).

## What changed vs. what's shared infrastructure

Retoned globally (per the project's "everything flows through tokens"
convention, `tailwind.config.ts`'s own top-of-file comment): `globals.css`
color/shadow/radius tokens, `tailwind.config.ts` fonts/radii, the two new
Google Fonts in `layout.tsx`. This means non-marketing pages (booking,
wallet, admin auth, etc.) inherit the new warm palette and rounding too —
expected, since those tokens are the single source of truth product-wide.

Rebuilt presentationally, marketing-scope only: `src/app/page.tsx`,
`src/app/categories/page.tsx`, `src/app/categories/[slug]/page.tsx`,
`src/app/services/[slug]/page.tsx`, and `HeroBanner`, `CategoryTile`,
`CategoryTiles`, `CategoryGridSkeleton`, `ServiceCard`, `ServiceGroupSection`
in `src/components/`. Booking flow, auth, profile, wallet,
admin/provider apps, and backend code were not touched.
