"use client";

import { createContext, forwardRef, useContext, useEffect, useId, useRef, useState } from "react";
import { createPortal } from "react-dom";
import type {
  ButtonHTMLAttributes,
  InputHTMLAttributes,
  PointerEvent as ReactPointerEvent,
  ReactNode,
  SelectHTMLAttributes,
  TextareaHTMLAttributes,
} from "react";

/**
 * The Nestly component kit.
 *
 * Every screen in this app is built from these primitives, and every visual
 * value they use resolves through the design tokens in app/globals.css — no
 * component here contains a hex code or a raw `neutral-*`/`black/10` class.
 * Restyling the product happens in the token layer, not here.
 *
 * This file is copied across customer-web, admin-web and provider-web (the
 * three apps are independent Next projects with no shared package), so it is
 * a deliberate superset: an app that never renders a `Table` still ships the
 * component. Changing a primitive means porting the change to all three.
 *
 * The three are NOT byte-identical, and have not been since customer-web grew
 * UI the other two never needed. This comment used to claim they were, which
 * sent a reader looking for a drift bug that was really a deliberate feature
 * (tasks.csv 336, 362). Current known deltas, listed here so nobody "fixes"
 * them by re-syncing the files:
 *
 *   1. `hideLabel` on `FieldShell`/`Field` (customer-web only) - renders the
 *      label `sr-only`, for the inline coupon-code field.
 *   2. `LinkButton` (customer-web only) - a `next/link` anchor sharing
 *      `BUTTON_VARIANTS` and `BUTTON_SIZES`, so a navigation control can look
 *      like a button without pretending to be one.
 *   3. A `danger-soft` entry in `BUTTON_VARIANTS` (customer-web only).
 *   4. `Modal` portals to `document.body` via `createPortal` - a
 *      `transform`/`filter`/`backdrop-filter` ancestor (e.g. `backdrop-blur-md`
 *      on customer-web's SiteHeader, or the persistent `transform` an
 *      `animate-rise` root leaves behind via `animation-fill-mode: both` on
 *      provider-web's pages) makes `fixed inset-0` position against that
 *      ancestor rather than the viewport. customer-web and provider-web both
 *      have this fix; admin-web does not yet and carries the same latent bug
 *      wherever a Modal opens under a transformed/filtered ancestor.
 *   5. The imports those need: `next/link`, `createPortal`,
 *      `AnchorHTMLAttributes` (customer-web only needs `next/link` and
 *      `AnchorHTMLAttributes`; `createPortal` is now shared by customer-web
 *      and provider-web).
 *
 * Porting one of those INTO another app is fine. Deleting it to make a diff
 * come out clean is not.
 */

/** Joins conditional class names, dropping falsy entries. */
export function cx(...parts: Array<string | false | null | undefined>): string {
  return parts.filter(Boolean).join(" ");
}

/* -------------------------------------------------------------------------- */
/* Surfaces                                                                   */
/* -------------------------------------------------------------------------- */

interface CardProps {
  /** Optional: a card used purely as a container needs no heading. */
  title?: string;
  description?: string;
  /** Right-aligned controls in the card header (links, menus, small buttons). */
  actions?: ReactNode;
  footer?: ReactNode;
  children: ReactNode;
  /** Turn off the body padding for edge-to-edge content such as a `Table`. */
  flush?: boolean;
  className?: string;
}

export function Card({
  title,
  description,
  actions,
  footer,
  children,
  flush = false,
  className = "",
}: CardProps) {
  const hasHeader = Boolean(title || description || actions);

  return (
    <section
      className={cx(
        "w-full overflow-hidden rounded-2xl bg-surface shadow-sm",
        className,
      )}
    >
      {hasHeader ? (
        <div className="flex items-start justify-between gap-4 px-6 pt-6">
          <div className="min-w-0">
            {/* 18px/600 matches the MatDash reference's card-title typography exactly. */}
            {title ? <h2 className="text-lg font-semibold text-fg">{title}</h2> : null}
            {description ? (
              <p className="mt-1 text-sm leading-relaxed text-fg-muted">{description}</p>
            ) : null}
          </div>
          {actions ? <div className="flex shrink-0 items-center gap-2">{actions}</div> : null}
        </div>
      ) : null}

      <div className={cx(!flush && "px-6 pb-6", hasHeader ? "mt-5" : !flush && "pt-6")}>
        {children}
      </div>

      {footer ? (
        <div className="border-t border-line bg-surface-2 px-6 py-4 text-sm text-fg-muted">
          {footer}
        </div>
      ) : null}
    </section>
  );
}

/** Hairline rule with the standard token colour. */
export function Divider({ className = "" }: { className?: string }) {
  return <hr className={cx("border-0 border-t border-line", className)} />;
}

/* -------------------------------------------------------------------------- */
/* Feedback                                                                   */
/* -------------------------------------------------------------------------- */

const ALERT_TONES = {
  error: "border-danger/25 bg-danger-soft text-danger",
  success: "border-success/25 bg-success-soft text-success",
  info: "border-info/25 bg-info-soft text-info",
  warning: "border-warning/25 bg-warning-soft text-warning",
} as const;

export function Alert({
  tone = "error",
  title,
  action,
  children,
}: {
  tone?: keyof typeof ALERT_TONES;
  title?: string;
  /** Recovery affordance — a Retry button belongs here, not in the message. */
  action?: ReactNode;
  children: ReactNode;
}) {
  return (
    <div
      // Errors interrupt; everything else is announced politely when convenient.
      role={tone === "error" ? "alert" : "status"}
      className={cx(
        "flex items-start gap-3 rounded-xl border px-4 py-3 text-sm",
        ALERT_TONES[tone],
      )}
    >
      <AlertIcon tone={tone} />
      <div className="min-w-0 flex-1">
        {title ? <p className="font-semibold">{title}</p> : null}
        <div className={cx("leading-relaxed", title && "mt-0.5 opacity-90")}>{children}</div>
      </div>
      {action ? <div className="shrink-0">{action}</div> : null}
    </div>
  );
}

function AlertIcon({ tone }: { tone: keyof typeof ALERT_TONES }) {
  const path =
    tone === "success"
      ? "m5 13 4 4L19 7"
      : tone === "info"
        ? "M12 16v-5M12 8h.01"
        : "M12 9v4M12 17h.01";

  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="mt-0.5 h-4 w-4 shrink-0"
      aria-hidden
    >
      {tone === "success" ? null : <circle cx="12" cy="12" r="9" />}
      <path d={path} />
    </svg>
  );
}

const BADGE_TONES = {
  neutral: "bg-surface-3 text-fg-muted",
  brand: "bg-brand-50 text-brand-700 dark:bg-brand-500/15 dark:text-brand-300",
  success: "bg-success-soft text-success",
  warning: "bg-warning-soft text-warning",
  danger: "bg-danger-soft text-danger",
  info: "bg-info-soft text-info",
  accent: "bg-accent-100 text-accent-700 dark:bg-accent-500/15 dark:text-accent-300",
} as const;

export type BadgeTone = keyof typeof BADGE_TONES;

/** Compact status pill — booking states, roles, KYC stages. */
export function Badge({
  tone = "neutral",
  children,
  className = "",
}: {
  tone?: BadgeTone;
  children: ReactNode;
  className?: string;
}) {
  return (
    <span
      className={cx(
        // Soft-pill, no border/ring - matches the MatDash reference's
        // status badges exactly (computed padding/size/weight already did).
        "inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-medium",
        BADGE_TONES[tone],
        className,
      )}
    >
      {children}
    </span>
  );
}

const AVATAR_TONES = [
  "bg-brand-100 text-brand-700 dark:bg-brand-500/20 dark:text-brand-300",
  "bg-accent-100 text-accent-700 dark:bg-accent-500/20 dark:text-accent-300",
  "bg-success-soft text-success",
  "bg-info-soft text-info",
  "bg-danger-soft text-danger",
  "bg-warning-soft text-warning",
] as const;

/** Deterministic tone from a name, so the same person always gets the same color. */
function toneForName(name: string): string {
  let hash = 0;
  for (let index = 0; index < name.length; index += 1) {
    hash = (hash * 31 + name.charCodeAt(index)) | 0;
  }
  return AVATAR_TONES[Math.abs(hash) % AVATAR_TONES.length];
}

function initialsFor(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return "?";
  const first = parts[0][0] ?? "";
  const last = parts.length > 1 ? (parts[parts.length - 1][0] ?? "") : "";
  return (first + last).toUpperCase();
}

/**
 * Initials avatar chip — the colored-circle-next-to-a-name treatment every
 * MatDash list row uses. Color is derived from the name (not random) so a
 * given customer/provider reads consistently across screens.
 */
export function Avatar({
  name,
  size = "md",
  className = "",
}: {
  name: string;
  size?: "sm" | "md";
  className?: string;
}) {
  return (
    <span
      aria-hidden
      className={cx(
        "inline-flex shrink-0 items-center justify-center rounded-full font-semibold",
        size === "sm" ? "h-7 w-7 text-[0.6875rem]" : "h-9 w-9 text-xs",
        toneForName(name),
        className,
      )}
    >
      {initialsFor(name)}
    </span>
  );
}

export function Spinner({ className = "" }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      className={cx("h-4 w-4 animate-spin", className)}
      aria-hidden
      fill="none"
    >
      <circle cx="12" cy="12" r="9" stroke="currentColor" strokeWidth="2.5" opacity="0.2" />
      <path
        d="M21 12a9 9 0 0 0-9-9"
        stroke="currentColor"
        strokeWidth="2.5"
        strokeLinecap="round"
      />
    </svg>
  );
}

/**
 * Loading placeholder. Always size these to the real content's dimensions —
 * a skeleton that doesn't match what replaces it causes a layout jump, which
 * is worse than no skeleton at all.
 */
export function Skeleton({ className = "" }: { className?: string }) {
  return (
    <div
      className={cx(
        "relative overflow-hidden rounded-lg bg-surface-3",
        "after:absolute after:inset-0 after:animate-shimmer after:bg-skeleton-shimmer after:bg-[length:200%_100%] after:content-['']",
        className,
      )}
      aria-hidden
    />
  );
}

/** Multi-line text skeleton with a short final line, the way real text wraps. */
export function SkeletonText({ lines = 3, className = "" }: { lines?: number; className?: string }) {
  return (
    <div className={cx("flex flex-col gap-2", className)} aria-hidden>
      {Array.from({ length: lines }, (_, index) => (
        <Skeleton
          key={index}
          className={cx("h-3.5", index === lines - 1 ? "w-2/3" : "w-full")}
        />
      ))}
    </div>
  );
}

/**
 * Terminal state for a screen with nothing to show. `action` is not optional
 * in spirit: an empty state without a next step is a dead end.
 */
export function EmptyState({
  icon,
  title,
  description,
  action,
  className = "",
}: {
  icon?: ReactNode;
  title: string;
  description?: string;
  action?: ReactNode;
  className?: string;
}) {
  return (
    <div
      // Polite live region: this is what mounts once a search, filter or
      // load settles on "nothing to show," and that result deserves the same
      // announcement a screen-reader user gets for a non-empty result set.
      // Harmless on a page where this is present from first paint too - most
      // screen readers only announce a status region's *changes*, not
      // content already there when the page settles.
      role="status"
      className={cx(
        "flex flex-col items-center justify-center rounded-2xl border border-dashed border-line px-6 py-14 text-center",
        className,
      )}
    >
      {icon ? (
        <div className="mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-surface-3 text-fg-subtle">
          {icon}
        </div>
      ) : null}
      <p className="text-base font-semibold text-fg">{title}</p>
      {description ? (
        <p className="mt-1.5 max-w-sm text-sm leading-relaxed text-fg-muted">{description}</p>
      ) : null}
      {action ? <div className="mt-6">{action}</div> : null}
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Form controls                                                              */
/* -------------------------------------------------------------------------- */

/**
 * One control skin for input/select/textarea so a form reads as a single
 * system, matched to the MatDash/shadcn form-input reference
 * (matdash-nextjs-minisidebar.vercel.app/shadcn-form/input): flat (no
 * shadow), a plain 1px border with no hover-darken, and a focus state that
 * is a border-color shift alone - no glow ring - unlike this kit's other
 * focus treatments. Invalid controls carry the same flat, ring-less
 * language in the danger tone.
 */
// Radius matched to the MatDash reference's form controls (computed
// `border-radius: 6px`, i.e. this scale's `sm`) - distinct from `Button`'s
// own reference-matched radius (9px, see BUTTON_VARIANTS' comment).
const CONTROL_BASE =
  "w-full rounded-sm border bg-surface px-3 py-2 text-sm text-fg outline-none transition duration-fast ease-out placeholder:text-fg-subtle disabled:cursor-not-allowed disabled:bg-surface-2 disabled:text-fg-subtle";
// h-10 (the reference's fixed 40px) is appended only where a control is
// single-line (Field/Select) - Textarea keeps CONTROL_BASE's height auto so
// its `rows` prop still controls its size.
const CONTROL_FIXED_HEIGHT = "h-10";
const CONTROL_IDLE = "border-line focus:border-brand-600";
const CONTROL_INVALID = "border-danger focus:border-danger";

/** Shared label/hint/error scaffolding so every control is described identically. */
function FieldShell({
  id,
  label,
  hint,
  error,
  required,
  children,
}: {
  id: string;
  label: string;
  hint?: string;
  error?: string;
  required?: boolean;
  children: ReactNode;
}) {
  return (
    <div className="flex flex-col gap-2">
      <label htmlFor={id} className="text-sm font-semibold text-fg">
        {label}
        {required ? (
          <span className="ml-0.5 text-danger" aria-hidden>
            *
          </span>
        ) : null}
      </label>
      {children}
      {error ? (
        <p id={`${id}-error`} className="text-xs font-medium text-danger">
          {error}
        </p>
      ) : hint ? (
        <p id={`${id}-hint`} className="text-xs text-fg-muted">
          {hint}
        </p>
      ) : null}
    </div>
  );
}

/**
 * Derives a stable id from the field name so label/control always agree.
 *
 * Falls back to `reactId` (each caller's own `useId()`) rather than a
 * label-derived slug: two controls with the same visible label (e.g. two
 * "Reason" fields in different cards on one page, as bookings/[bookingId]
 * has for cancel vs. refund) used to collide on the same `field-reason` id,
 * producing duplicate DOM ids and an ambiguous `label[for]` association.
 * `useId()` is per-component-instance and therefore always unique, while
 * still leaving an explicit `id`/`name` free to opt into a predictable,
 * human-readable id where one is actually wanted (CSS hooks, e2e selectors).
 */
function controlId(explicit: string | undefined, name: string | undefined, reactId: string): string {
  return explicit ?? (name ? `field-${name}` : reactId);
}

interface FieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string;
  error?: string;
  hint?: string;
  /**
   * Leading adornment — a ₹ sign, a search glyph, a country code. Named
   * `leading` rather than `prefix` because `prefix` is a real HTML attribute
   * (typed `string`) that this interface would otherwise clash with.
   */
  leading?: ReactNode;
  /**
   * Trailing adornment — e.g. a show/hide-password toggle. Positioned
   * relative to the input box itself (not the label above it), so it stays
   * vertically centered regardless of label/error text height - unlike a
   * caller wrapping the whole `Field` in its own `relative` and guessing a
   * fixed top offset, which drifts out of alignment the moment this
   * component's label styling changes. Unlike `leading` this is interactive
   * (not `pointer-events-none`); callers supply their own button.
   */
  trailing?: ReactNode;
}

export const Field = forwardRef<HTMLInputElement, FieldProps>(function Field(
  { label, error, hint, leading, trailing, id, className = "", ...props },
  ref,
) {
  const reactId = useId();
  const inputId = controlId(id, props.name, reactId);

  const input = (
    <input
      {...props}
      id={inputId}
      ref={ref}
      aria-invalid={error ? true : undefined}
      aria-describedby={error ? `${inputId}-error` : hint ? `${inputId}-hint` : undefined}
      className={cx(
        CONTROL_BASE,
        CONTROL_FIXED_HEIGHT,
        error ? CONTROL_INVALID : CONTROL_IDLE,
        Boolean(leading) && "pl-9",
        Boolean(trailing) && "pr-10",
        className,
      )}
    />
  );

  return (
    <FieldShell
      id={inputId}
      label={label}
      hint={hint}
      error={error}
      required={props.required}
    >
      {leading || trailing ? (
        <div className="relative">
          {leading ? (
            <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-sm text-fg-subtle">
              {leading}
            </span>
          ) : null}
          {input}
          {trailing ? (
            <span className="absolute right-1 top-1/2 -translate-y-1/2">{trailing}</span>
          ) : null}
        </div>
      ) : (
        input
      )}
    </FieldShell>
  );
});

interface TextareaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label: string;
  error?: string;
  hint?: string;
}

/** Multi-line counterpart to `Field`, for long free-text (descriptions, notes). */
export const Textarea = forwardRef<HTMLTextAreaElement, TextareaProps>(function Textarea(
  { label, error, hint, id, rows = 3, className = "", ...props },
  ref,
) {
  const reactId = useId();
  const textareaId = controlId(id, props.name, reactId);

  return (
    <FieldShell
      id={textareaId}
      label={label}
      hint={hint}
      error={error}
      required={props.required}
    >
      <textarea
        {...props}
        id={textareaId}
        ref={ref}
        rows={rows}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${textareaId}-error` : hint ? `${textareaId}-hint` : undefined}
        className={cx(
          CONTROL_BASE,
          "resize-y leading-relaxed",
          error ? CONTROL_INVALID : CONTROL_IDLE,
          className,
        )}
      />
    </FieldShell>
  );
});

interface SelectOption {
  value: string;
  label: string;
}

interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  label: string;
  error?: string;
  hint?: string;
  options: readonly SelectOption[];
  /** Disabled first option, e.g. "Select a state…" — choosing it keeps the field empty. */
  placeholder?: string;
}

export const Select = forwardRef<HTMLSelectElement, SelectProps>(function Select(
  { label, error, hint, id, options, placeholder, className = "", ...props },
  ref,
) {
  const reactId = useId();
  const selectId = controlId(id, props.name, reactId);

  return (
    <FieldShell id={selectId} label={label} hint={hint} error={error} required={props.required}>
      <div className="relative">
        <select
          {...props}
          id={selectId}
          ref={ref}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? `${selectId}-error` : hint ? `${selectId}-hint` : undefined}
          className={cx(
            CONTROL_BASE,
            CONTROL_FIXED_HEIGHT,
            // Room for the custom chevron; the native one is hidden so the
            // control matches Field/Textarea across platforms.
            "appearance-none pr-9",
            error ? CONTROL_INVALID : CONTROL_IDLE,
            className,
          )}
        >
          {placeholder ? (
            <option value="" disabled>
              {placeholder}
            </option>
          ) : null}
          {options.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
          className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 text-fg-subtle"
          aria-hidden
        >
          <path d="m6 9 6 6 6-6" />
        </svg>
      </div>
    </FieldShell>
  );
});

export const Checkbox = forwardRef<
  HTMLInputElement,
  InputHTMLAttributes<HTMLInputElement> & { label: string }
>(function Checkbox({ label, ...props }, ref) {
  const reactId = useId();
  const inputId = controlId(props.id, props.name, reactId);
  return (
    <label
      htmlFor={inputId}
      // Text-only label sits well under the 44px touch-target minimum;
      // hit-slop pads the tap area instead of adding visible row spacing.
      // Callers stacking these should keep >=12px gap between rows so
      // adjacent hit-slop zones don't overlap.
      className="relative flex cursor-pointer items-center gap-2.5 text-sm text-fg after:absolute after:-inset-3 after:content-['']"
    >
      <input
        {...props}
        ref={ref}
        id={inputId}
        type="checkbox"
        className="h-4 w-4 shrink-0 cursor-pointer rounded border-line-strong text-brand-600 accent-brand-600"
      />
      {label}
    </label>
  );
});

interface CheckboxFieldProps {
  label: string;
  description?: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}

/**
 * Boxed boolean toggle with a description. Controlled rather than a
 * forwardRef input like `Field` — boolean form values come through
 * react-hook-form's `Controller`, and this shape matches that call site.
 */
export function CheckboxField({
  label,
  description,
  checked,
  onChange,
  disabled,
}: CheckboxFieldProps) {
  return (
    <label
      className={cx(
        "flex cursor-pointer items-start gap-3 rounded-xl border px-4 py-3 text-sm transition duration-fast ease-out",
        checked
          ? "border-brand-600/40 bg-brand-50 dark:bg-brand-500/10"
          : "border-line bg-surface hover:border-line-strong hover:bg-surface-2",
        disabled && "cursor-not-allowed opacity-60",
      )}
    >
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        disabled={disabled}
        className="mt-0.5 h-4 w-4 shrink-0 cursor-pointer rounded border-line-strong accent-brand-600"
      />
      <span className="flex min-w-0 flex-col">
        <span className="font-medium text-fg">{label}</span>
        {description ? (
          <span className="mt-0.5 text-xs leading-relaxed text-fg-muted">{description}</span>
        ) : null}
      </span>
    </label>
  );
}

/* -------------------------------------------------------------------------- */
/* Actions                                                                    */
/* -------------------------------------------------------------------------- */

// Shape matched to the MatDash/shadcn reference's button page
// (matdash-nextjs-minisidebar.vercel.app/shadcn-ui/buttons: computed
// `border-radius: 9px`, h-10/px-4/py-2/text-sm on every variant - see the
// base `Button` className below for the radius/sizing match). The reference
// itself is flat (`box-shadow: none` on every variant, solid-fill Basic
// buttons with only a background-color hover shift). Rather than flatten
// Nestly's buttons to match that exactly, the design call here is to keep
// this kit's existing brand-glow depth and layer a hover "lift" on top of
// the reference's crisper corners - more decorative than the flat
// reference, not less, while still reading as the same visual family as
// the now-matched inputs/cards.
const BUTTON_VARIANTS = {
  primary:
    "bg-brand-600 text-fg-on-brand shadow-brand hover:bg-brand-700 hover:shadow-lg active:bg-brand-800 active:shadow-brand disabled:shadow-none",
  secondary:
    "border border-line bg-surface text-fg shadow-xs hover:border-line-strong hover:bg-surface-2 hover:shadow-sm active:bg-surface-3 active:shadow-xs",
  danger:
    "bg-danger text-white shadow-xs hover:brightness-95 hover:shadow-md active:brightness-90 active:shadow-xs dark:text-bg",
  ghost: "text-fg-muted hover:bg-surface-3 hover:text-fg active:bg-surface-3",
  subtle:
    "bg-brand-50 text-brand-700 hover:bg-brand-100 dark:bg-brand-500/15 dark:text-brand-300 dark:hover:bg-brand-500/25",
  link: "text-brand-600 underline-offset-4 hover:underline dark:text-brand-400",
} as const;

// 44x44 is the minimum touch target (Apple HIG / WCAG 2.5.5). `sm`/`md` fall
// short (32px/40px) but keep their compact visual box — dense UI like table
// row actions depends on that — so the shortfall is padded with a
// transparent `::after` hit-slop instead of growing the box. `lg` already
// clears 44px unaided.
const BUTTON_SIZES = {
  sm: "relative h-8 gap-1.5 px-3 text-xs after:absolute after:-inset-1.5 after:content-['']",
  md: "relative h-10 gap-2 px-4 text-sm after:absolute after:-inset-0.5 after:content-['']",
  lg: "h-12 gap-2 px-6 text-[0.9375rem]",
} as const;

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: keyof typeof BUTTON_VARIANTS;
  size?: keyof typeof BUTTON_SIZES;
  /** Shows a spinner and blocks input — use for any in-flight submit. */
  loading?: boolean;
  fullWidth?: boolean;
  /** Rendered before the label; hidden while `loading` so width stays stable. */
  icon?: ReactNode;
}

export function Button({
  variant = "primary",
  size = "md",
  loading = false,
  fullWidth = false,
  icon,
  className = "",
  children,
  disabled,
  ...props
}: ButtonProps) {
  return (
    <button
      {...props}
      // A loading button must not be re-submittable; `aria-busy` tells
      // assistive tech the action is running rather than simply unavailable.
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={cx(
        "inline-flex select-none items-center justify-center whitespace-nowrap rounded-[9px] font-medium transition duration-fast ease-out",
        "disabled:cursor-not-allowed disabled:opacity-55",
        // Tiny scale on press reads as physical without moving layout.
        "active:scale-[0.98] disabled:active:scale-100",
        BUTTON_VARIANTS[variant],
        BUTTON_SIZES[size],
        fullWidth && "w-full",
        className,
      )}
    >
      {loading ? <Spinner /> : icon}
      {children}
    </button>
  );
}

/** Square icon-only button. `label` is required — it becomes the accessible name. */
export function IconButton({
  label,
  variant = "ghost",
  className = "",
  children,
  ...props
}: Omit<ButtonHTMLAttributes<HTMLButtonElement>, "children"> & {
  label: string;
  variant?: keyof typeof BUTTON_VARIANTS;
  children: ReactNode;
}) {
  return (
    <button
      {...props}
      aria-label={label}
      title={label}
      className={cx(
        // 36px box is short of the 44px touch-target minimum; same hit-slop
        // approach as BUTTON_SIZES keeps the icon chip's visual size intact.
        "relative inline-flex h-9 w-9 items-center justify-center rounded-[9px] transition duration-fast ease-out disabled:cursor-not-allowed disabled:opacity-55 after:absolute after:-inset-1 after:content-['']",
        BUTTON_VARIANTS[variant],
        className,
      )}
    >
      {children}
    </button>
  );
}

/* -------------------------------------------------------------------------- */
/* Page structure                                                             */
/* -------------------------------------------------------------------------- */

export function PageHeading({
  title,
  subtitle,
  actions,
  breadcrumbs,
}: {
  title: string;
  subtitle?: string;
  actions?: ReactNode;
  breadcrumbs?: ReactNode;
}) {
  return (
    <header className="mb-6 flex w-full flex-col gap-4 rounded-2xl bg-surface p-6 shadow-sm sm:flex-row sm:items-end sm:justify-between">
      <div className="min-w-0">
        {breadcrumbs ? <div className="mb-2">{breadcrumbs}</div> : null}
        <h1 className="text-sm font-semibold text-fg sm:text-base">{title}</h1>
        {subtitle ? (
          <p className="mt-1.5 max-w-2xl text-sm leading-relaxed text-fg-muted">{subtitle}</p>
        ) : null}
      </div>
      {actions ? <div className="flex shrink-0 items-center gap-2">{actions}</div> : null}
    </header>
  );
}

/**
 * A single KPI number. The value renders in the standard text tokens — the
 * tile's border carries no meaning of its own, so nothing here is coloured by
 * the data the way a chart mark would be. `delta` is the one exception, where
 * direction genuinely is the information.
 */
export function StatTile({
  label,
  value,
  title,
  hint,
  delta,
  tone,
}: {
  label: string;
  value: string;
  title?: string;
  hint?: string;
  delta?: { value: string; direction: "up" | "down" };
  /** Whole-tile pastel tint, matching `KpiCard` (task: Modernize dashboard
   *  reference's stat-card row). Omit for the plain neutral tile every
   *  report/summary screen already used before that pass. */
  tone?: ChartTone;
}) {
  return (
    <div
      className={cx(
        "rounded-2xl p-5 shadow-sm",
        tone ? KPI_CARD_TONES[tone] : "bg-surface",
      )}
    >
      <p className={cx("text-sm font-medium", tone ? KPI_TEXT_TONES[tone] : "text-fg-muted")}>{label}</p>
      <div className="mt-2 flex items-baseline gap-2">
        <p
          className={cx(
            "nums min-w-0 truncate text-3xl font-semibold",
            tone ? KPI_TEXT_TONES[tone] : "text-fg",
          )}
          title={title}
        >
          {value}
        </p>
        {delta ? (
          <span
            className={cx(
              "text-xs font-medium",
              delta.direction === "up" ? "text-success" : "text-danger",
            )}
          >
            {delta.direction === "up" ? "↑" : "↓"} {delta.value}
          </span>
        ) : null}
      </div>
      {hint ? (
        <p className={cx("mt-1 text-xs", tone ? KPI_TEXT_TONES[tone] : "text-fg-subtle")}>{hint}</p>
      ) : null}
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Charts                                                                     */
/* -------------------------------------------------------------------------- */

/**
 * Hand-rolled SVG chart primitives rather than a charting dependency: the
 * product ships three of these numbers-and-trend surfaces per dashboard at
 * most, all fed by data the caller already fetched, so a full charting
 * library would be paid for once and used for axis ticks and legends this
 * app doesn't want. Every chart here is presentational only — no tooltips,
 * no interactivity — matching the KPI dashboards it decorates.
 */

const CHART_TONES = {
  brand: "rgb(var(--brand-600))",
  accent: "rgb(var(--accent-500))",
  success: "rgb(var(--success))",
  danger: "rgb(var(--danger))",
  info: "rgb(var(--info))",
  warning: "rgb(var(--warning))",
} as const;

export type ChartTone = keyof typeof CHART_TONES;

function normalizePoints(values: readonly number[], width: number, height: number, pad = 2) {
  if (values.length === 0) return [] as { x: number; y: number }[];
  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = max - min || 1;
  const step = values.length > 1 ? (width - pad * 2) / (values.length - 1) : 0;
  return values.map((value, index) => ({
    x: values.length > 1 ? pad + index * step : width / 2,
    y: pad + (height - pad * 2) * (1 - (value - min) / range),
  }));
}

/** Tiny inline trend line — the mark inside a `KpiCard`, not a standalone chart. */
export function Sparkline({
  values,
  tone = "brand",
  width = 96,
  height = 32,
  className = "",
}: {
  values: readonly number[];
  tone?: ChartTone;
  width?: number;
  height?: number;
  className?: string;
}) {
  const points = normalizePoints(values, width, height);
  if (points.length < 2) return null;
  const path = points.map((point, index) => `${index === 0 ? "M" : "L"}${point.x} ${point.y}`).join(" ");

  return (
    <svg
      viewBox={`0 0 ${width} ${height}`}
      width={width}
      height={height}
      className={cx("overflow-visible", className)}
      aria-hidden
    >
      <path d={path} fill="none" stroke={CHART_TONES[tone]} strokeWidth={1.75} strokeLinecap="round" strokeLinejoin="round" />
      <circle cx={points[points.length - 1].x} cy={points[points.length - 1].y} r={2} fill={CHART_TONES[tone]} />
    </svg>
  );
}

/**
 * Filled trend chart for a dashboard's headline series (bookings/revenue over
 * time). `labels` render along the x-axis when given; omit them for a dense
 * sparkline-scale chart with no axis.
 */
export function AreaChart({
  values,
  labels,
  tone = "brand",
  height = 220,
  className = "",
}: {
  values: readonly number[];
  labels?: readonly string[];
  tone?: ChartTone;
  height?: number;
  className?: string;
}) {
  const width = 640;
  const padBottom = labels ? 24 : 4;
  const points = normalizePoints(values, width, height - padBottom, 4);
  const gradientId = useId().replace(/:/g, "");

  if (points.length < 2) {
    return (
      <div
        className={cx("flex items-center justify-center text-sm text-fg-subtle", className)}
        style={{ height }}
      >
        Not enough data yet
      </div>
    );
  }

  const linePath = points.map((point, index) => `${index === 0 ? "M" : "L"}${point.x} ${point.y}`).join(" ");
  const areaPath = `${linePath} L${points[points.length - 1].x} ${height - padBottom} L${points[0].x} ${height - padBottom} Z`;
  const color = CHART_TONES[tone];

  return (
    <svg viewBox={`0 0 ${width} ${height}`} className={cx("w-full", className)} preserveAspectRatio="none" role="img" aria-label="Trend chart">
      <defs>
        <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity="0.28" />
          <stop offset="100%" stopColor={color} stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={areaPath} fill={`url(#${gradientId})`} />
      <path d={linePath} fill="none" stroke={color} strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" />
      {labels
        ? labels.map((label, index) => {
            const point = points[index];
            if (!point || (labels.length > 8 && index % Math.ceil(labels.length / 8) !== 0)) return null;
            return (
              <text
                key={index}
                x={point.x}
                y={height - 6}
                textAnchor="middle"
                className="fill-fg-subtle text-[10px]"
              >
                {label}
              </text>
            );
          })
        : null}
    </svg>
  );
}

/** Proportional breakdown (revenue by category, jobs by status) as a ring. */
export function DonutChart({
  data,
  size = 140,
  strokeWidth = 18,
}: {
  data: readonly { label: string; value: number; tone: ChartTone }[];
  size?: number;
  strokeWidth?: number;
}) {
  const total = data.reduce((sum, slice) => sum + slice.value, 0);
  const radius = (size - strokeWidth) / 2;
  const circumference = 2 * Math.PI * radius;
  let offset = 0;

  return (
    <div className="flex items-center gap-5">
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} className="shrink-0 -rotate-90" role="img" aria-label="Breakdown chart">
        <circle cx={size / 2} cy={size / 2} r={radius} fill="none" stroke="rgb(var(--surface-3))" strokeWidth={strokeWidth} />
        {total > 0
          ? data.map((slice, index) => {
              const fraction = slice.value / total;
              const dash = fraction * circumference;
              const circle = (
                <circle
                  key={index}
                  cx={size / 2}
                  cy={size / 2}
                  r={radius}
                  fill="none"
                  stroke={CHART_TONES[slice.tone]}
                  strokeWidth={strokeWidth}
                  strokeDasharray={`${dash} ${circumference - dash}`}
                  strokeDashoffset={-offset}
                  strokeLinecap="butt"
                />
              );
              offset += dash;
              return circle;
            })
          : null}
      </svg>
      <ul className="flex min-w-0 flex-col gap-2 text-sm">
        {data.map((slice, index) => (
          <li key={index} className="flex items-center gap-2 text-fg-muted">
            <span
              aria-hidden
              className="h-2 w-2 shrink-0 rounded-full"
              style={{ backgroundColor: CHART_TONES[slice.tone] }}
            />
            <span className="min-w-0 truncate">{slice.label}</span>
            <span className="ml-auto shrink-0 nums font-medium text-fg">
              {total > 0 ? Math.round((slice.value / total) * 100) : 0}%
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* KPI cards                                                                  */
/* -------------------------------------------------------------------------- */

/**
 * Whole-card pastel treatment (task: match the Modernize dashboard
 * reference's stat-card row - `.MuiCardContent` on a solid tint, icon sitting
 * directly on it, label and number both set in the tone's saturated color).
 */
const KPI_CARD_TONES = {
  brand: "bg-brand-50 dark:bg-brand-500/15",
  accent: "bg-accent-100 dark:bg-accent-500/15",
  success: "bg-success-soft",
  danger: "bg-danger-soft",
  info: "bg-info-soft",
  warning: "bg-warning-soft",
} as const;

const KPI_TEXT_TONES = {
  brand: "text-brand-600 dark:text-brand-300",
  accent: "text-accent-700 dark:text-accent-300",
  success: "text-success",
  danger: "text-danger",
  info: "text-info",
  warning: "text-warning",
} as const;

/**
 * Dashboard KPI card: a solid pastel tint per card with the icon sitting
 * directly on it and the label/number set in that same saturated tone -
 * matches the Modernize dashboard reference's stat-card row. Sits alongside
 * `StatTile` rather than replacing it — `StatTile` is the plain, chart-free
 * tile already used across list-screen headers, and this is the richer
 * dashboard-grade variant.
 */
export function KpiCard({
  icon,
  tone = "brand",
  label,
  value,
  delta,
  trend,
  className = "",
}: {
  icon: ReactNode;
  tone?: ChartTone;
  label: string;
  value: string;
  delta?: { value: string; direction: "up" | "down" };
  trend?: readonly number[];
  className?: string;
}) {
  return (
    <div
      className={cx(
        "rounded-2xl p-6 shadow-sm transition-shadow duration-fast ease-out hover:shadow-md",
        KPI_CARD_TONES[tone],
        className,
      )}
    >
      <div className="flex items-start justify-between gap-3">
        <span aria-hidden className={cx("flex h-10 w-10 shrink-0 items-center justify-center", KPI_TEXT_TONES[tone])}>
          {icon}
        </span>
        {trend && trend.length > 1 ? <Sparkline values={trend} tone={tone} /> : null}
      </div>
      <p className={cx("mt-4 text-sm font-medium", KPI_TEXT_TONES[tone])}>{label}</p>
      <div className="mt-1 flex items-baseline gap-2">
        <p className={cx("nums truncate text-2xl font-semibold", KPI_TEXT_TONES[tone])}>{value}</p>
        {delta ? (
          <span
            className={cx(
              "inline-flex items-center gap-0.5 text-xs font-medium",
              delta.direction === "up" ? "text-success" : "text-danger",
            )}
          >
            {delta.direction === "up" ? "↑" : "↓"} {delta.value}
          </span>
        ) : null}
      </div>
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Table                                                                      */
/* -------------------------------------------------------------------------- */

/**
 * Table primitives rather than a data-grid component: the admin modules each
 * need different cells, and a config-driven grid would be fought at every
 * call site. The wrapper owns the one thing every table must get right —
 * horizontal overflow scrolling inside its own container, so a wide table
 * never makes the page scroll sideways.
 */
export function Table({ children, className = "" }: { children: ReactNode; className?: string }) {
  return (
    <div className="w-full overflow-x-auto">
      <table className={cx("w-full min-w-full border-collapse text-sm", className)}>
        {children}
      </table>
    </div>
  );
}

export function THead({ children }: { children: ReactNode }) {
  return (
    <thead className="border-b border-line text-left">
      {children}
    </thead>
  );
}

export function TH({
  children,
  numeric = false,
  className = "",
}: {
  children: ReactNode;
  numeric?: boolean;
  className?: string;
}) {
  return (
    <th
      scope="col"
      className={cx(
        "whitespace-nowrap px-4 py-3 text-sm font-bold text-fg",
        numeric && "text-right",
        className,
      )}
    >
      {children}
    </th>
  );
}

export function TBody({ children }: { children: ReactNode }) {
  return <tbody className="divide-y divide-line">{children}</tbody>;
}

export function TR({
  children,
  onClick,
  className = "",
}: {
  children: ReactNode;
  onClick?: () => void;
  className?: string;
}) {
  return (
    <tr
      onClick={onClick}
      // A `<tr>` is not natively interactive: without tabIndex/onKeyDown a
      // clickable row is mouse-only, invisible to keyboard and screen-reader
      // users despite the pointer cursor telling sighted mouse users it acts
      // like a link.
      tabIndex={onClick ? 0 : undefined}
      onKeyDown={
        onClick
          ? (event) => {
              if (event.key !== "Enter" && event.key !== " ") return;
              event.preventDefault();
              onClick();
            }
          : undefined
      }
      className={cx(
        "transition-colors duration-fast ease-out",
        onClick ? "cursor-pointer hover:bg-surface-2" : "hover:bg-surface-2/60",
        className,
      )}
    >
      {children}
    </tr>
  );
}

export function TD({
  children,
  numeric = false,
  className = "",
}: {
  children: ReactNode;
  numeric?: boolean;
  className?: string;
}) {
  return (
    <td
      className={cx(
        "px-4 py-3 text-fg",
        // Tabular figures keep a column of amounts from jittering as it updates.
        numeric && "nums text-right",
        className,
      )}
    >
      {children}
    </td>
  );
}

/** Full-width message row for a table's empty/error state. */
export function TableMessage({ colSpan, children }: { colSpan: number; children: ReactNode }) {
  return (
    <tr>
      <td colSpan={colSpan} className="px-4 py-12 text-center text-sm text-fg-muted">
        {children}
      </td>
    </tr>
  );
}

/* -------------------------------------------------------------------------- */
/* Tabs                                                                       */
/* -------------------------------------------------------------------------- */

/**
 * Presentational tab strip. State stays with the caller so a tab can be driven
 * from the URL (which most of these screens should do) rather than trapped in
 * component state.
 */
export function Tabs<T extends string>({
  tabs,
  value,
  onChange,
  label,
  className = "",
}: {
  tabs: readonly { value: T; label: string; count?: number }[];
  value: T;
  onChange: (value: T) => void;
  /**
   * Accessible name for the tablist. Required in practice: a bare `tablist`
   * with no name is ambiguous to screen readers on any page carrying more
   * than one, and it is how tests address the group.
   */
  label?: string;
  className?: string;
}) {
  return (
    <div
      className={cx("flex gap-1 overflow-x-auto border-b border-line", className)}
      role="tablist"
      aria-label={label}
    >
      {tabs.map((tab) => {
        const isActive = tab.value === value;
        return (
          <button
            key={tab.value}
            type="button"
            role="tab"
            aria-selected={isActive}
            onClick={() => onChange(tab.value)}
            className={cx(
              // 40px tab is 4px short of the 44px touch-target minimum; same
              // hit-slop approach as BUTTON_SIZES (2px/side stays inside the
              // 4px `gap-1` between tabs, so slop zones never overlap).
              "relative whitespace-nowrap px-4 py-2.5 text-sm font-medium transition-colors duration-fast ease-out after:absolute after:-inset-0.5 after:content-['']",
              isActive ? "text-brand-600 dark:text-brand-400" : "text-fg-muted hover:text-fg",
            )}
          >
            {tab.label}
            {typeof tab.count === "number" ? (
              <span className="ml-1.5 text-xs text-fg-subtle">{tab.count}</span>
            ) : null}
            {isActive ? (
              // Sits on the container's border so the indicator reads as part
              // of the rule rather than floating above it.
              <span className="absolute inset-x-2 -bottom-px h-0.5 rounded-full bg-brand-600 dark:bg-brand-400" />
            ) : null}
          </button>
        );
      })}
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Modal                                                                      */
/* -------------------------------------------------------------------------- */

/**
 * Dialog with the three behaviours a hand-rolled modal usually misses:
 * Escape closes it, focus moves inside on open and returns to the trigger on
 * close, and Tab cycles within the dialog instead of escaping to the page
 * behind it.
 */
export function Modal({
  open,
  onClose,
  title,
  description,
  footer,
  children,
  size = "md",
}: {
  open: boolean;
  onClose: () => void;
  title: string;
  description?: string;
  footer?: ReactNode;
  children: ReactNode;
  size?: "sm" | "md" | "lg";
}) {
  const panelRef = useRef<HTMLDivElement>(null);
  const titleId = useId();
  const descriptionId = useId();

  // Swipe-to-dismiss for the mobile bottom-sheet state only (see the drag
  // handle below, which is the only element wired to these handlers and is
  // itself hidden from `sm:` up). `hasDragged` keeps the drag transform out
  // of the panel's inline style until a drag actually starts, so it never
  // fights the `animate-pop` entrance keyframes on open - real state, not a
  // ref, because it feeds the render below: a ref mutation on its own
  // wouldn't schedule a re-render, and reading .current during render is
  // exactly what react-hooks/refs exists to catch.
  const [dragOffset, setDragOffset] = useState(0);
  const [isDragging, setIsDragging] = useState(false);
  const [hasDragged, setHasDragged] = useState(false);
  const dragStartYRef = useRef<number | null>(null);

  const handleSheetDragStart = (event: ReactPointerEvent<HTMLDivElement>) => {
    event.currentTarget.setPointerCapture(event.pointerId);
    setHasDragged(true);
    dragStartYRef.current = event.clientY;
    setIsDragging(true);
  };

  const handleSheetDragMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (dragStartYRef.current === null) return;
    setDragOffset(Math.max(0, event.clientY - dragStartYRef.current));
  };

  // Released past ~30% of a typical sheet's visible height dismisses; short
  // of that snaps back via the transition set below.
  const handleSheetDragEnd = () => {
    if (dragOffset > 96) onClose();
    setIsDragging(false);
    setDragOffset(0);
    dragStartYRef.current = null;
  };

  // Callers overwhelmingly pass an inline `onClose` (a fresh function every
  // render), so keeping it out of the effect below matters: a ref lets the
  // keydown handler always call the latest `onClose` without making every
  // parent re-render (e.g. each keystroke updating form state) re-run the
  // effect and re-focus the dialog's first control, stealing focus back from
  // whatever the user was actually typing into.
  const onCloseRef = useRef(onClose);
  useEffect(() => {
    onCloseRef.current = onClose;
  });

  useEffect(() => {
    if (!open) return;

    const previouslyFocused = document.activeElement as HTMLElement | null;
    // Scrolling the page behind an open dialog is disorienting on desktop and
    // actively breaks the dialog on iOS.
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";

    const focusables = () =>
      Array.from(
        panelRef.current?.querySelectorAll<HTMLElement>(
          'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
        ) ?? [],
      );

    // Prefer the first real control; fall back to the panel itself so focus is
    // never left behind on the trigger in a dialog with no focusable content.
    const firstFocusable = focusables()[0];
    if (firstFocusable) {
      firstFocusable.focus();
    } else {
      panelRef.current?.focus();
    }

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        event.stopPropagation();
        onCloseRef.current();
        return;
      }
      if (event.key !== "Tab") return;

      const items = focusables();
      if (items.length === 0) return;

      const first = items[0];
      const last = items[items.length - 1];
      const active = document.activeElement;

      // Wrap in both directions; without this, Tab walks out of the dialog
      // into the inert page behind it.
      if (event.shiftKey && (active === first || !panelRef.current?.contains(active))) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && active === last) {
        event.preventDefault();
        first.focus();
      }
    };

    document.addEventListener("keydown", onKeyDown, true);
    return () => {
      document.removeEventListener("keydown", onKeyDown, true);
      document.body.style.overflow = previousOverflow;
      previouslyFocused?.focus();
    };
    // `onClose` intentionally excluded - see onCloseRef above. Re-running this
    // only on `open` also means the focus-on-open behavior fires once per
    // open, not once per parent render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  if (!open) return null;

  const sizes = { sm: "max-w-sm", md: "max-w-lg", lg: "max-w-2xl" } as const;

  // Portalled to <body> rather than rendered in place: `fixed inset-0` only
  // covers the actual viewport if every ancestor is un-filtered/untransformed
  // - a `transform`/`filter`/`backdrop-filter` anywhere up the tree (e.g. a
  // page root wrapped in the `animate-rise` entrance animation, which leaves
  // a persistent `transform: matrix(...)` behind via `animation-fill-mode:
  // both`) makes that ancestor the containing block instead, so a Modal
  // opened from inside it renders squashed into that ancestor's box instead
  // of centered on the page. A portal sidesteps the whole class of bug
  // instead of requiring every future trigger location to stay
  // filter/transform-free above it. (Same fix as customer-web's Modal.)
  return createPortal(
    <div className="fixed inset-0 z-50 flex items-end justify-center p-0 sm:items-center sm:p-6">
      <div
        className="absolute inset-0 animate-fade-in bg-overlay/50 backdrop-blur-[2px]"
        onClick={onClose}
        aria-hidden
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={description ? descriptionId : undefined}
        tabIndex={-1}
        className={cx(
          // Borderless, shadow-only - matches the Card/DataTable treatment
          // (and the MatDash reference's card style generally).
          "relative w-full animate-pop rounded-t-2xl bg-surface shadow-xl outline-none sm:rounded-2xl",
          // Clears an iPhone's home-indicator bar in the bottom-sheet state;
          // reset on desktop where the dialog is centered, not sheet-anchored.
          "pb-[env(safe-area-inset-bottom)] sm:pb-0",
          sizes[size],
        )}
        style={
          hasDragged
            ? {
                transform: `translateY(${dragOffset}px)`,
                transition: isDragging ? "none" : "transform 200ms ease-out",
              }
            : undefined
        }
      >
        {/* Drag handle: swipe-to-dismiss in the mobile bottom-sheet state
            only — hidden from `sm:` up, where there is no sheet to drag. */}
        <div
          className="flex touch-none justify-center pb-1 pt-2 sm:hidden"
          onPointerDown={handleSheetDragStart}
          onPointerMove={handleSheetDragMove}
          onPointerUp={handleSheetDragEnd}
          onPointerCancel={handleSheetDragEnd}
        >
          <span className="h-1.5 w-10 rounded-full bg-line-strong" aria-hidden />
        </div>

        <div className="flex items-start justify-between gap-4 px-6 pt-6">
          <div className="min-w-0">
            <h2 id={titleId} className="text-lg font-semibold text-fg">
              {title}
            </h2>
            {description ? (
              <p id={descriptionId} className="mt-1 text-sm leading-relaxed text-fg-muted">
                {description}
              </p>
            ) : null}
          </div>
          <IconButton label="Close" onClick={onClose} className="-mr-2 -mt-2 shrink-0">
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              className="h-4 w-4"
              aria-hidden
            >
              <path d="M18 6 6 18M6 6l12 12" />
            </svg>
          </IconButton>
        </div>

        <div className="max-h-[70vh] overflow-y-auto px-6 py-5">{children}</div>

        {footer ? (
          <div className="flex justify-end gap-3 border-t border-line bg-surface-2 px-6 py-4">
            {footer}
          </div>
        ) : null}
      </div>
    </div>,
    document.body,
  );
}

/* -------------------------------------------------------------------------- */
/* Toast                                                                      */
/* -------------------------------------------------------------------------- */

interface Toast {
  id: number;
  tone: keyof typeof ALERT_TONES;
  message: string;
  /** Set once its timer expires; drives the exit transition before removal. */
  leaving: boolean;
}

// Must be >= the CSS exit transition duration below (duration-fast = 120ms)
// so the toast is fully faded before it is removed from state.
const TOAST_EXIT_MS = 150;

const ToastContext = createContext<((tone: Toast["tone"], message: string) => void) | null>(null);

/**
 * Transient confirmations ("Address saved"), mounted once near the app root.
 * Deliberately minimal: anything the user must act on belongs in an `Alert`
 * on the page, not in something that disappears on a timer.
 */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const nextId = useRef(0);

  const push = (tone: Toast["tone"], message: string) => {
    const id = nextId.current++;
    setToasts((current) => [...current, { id, tone, message, leaving: false }]);
    window.setTimeout(() => {
      setToasts((current) =>
        current.map((toast) => (toast.id === id ? { ...toast, leaving: true } : toast)),
      );
      window.setTimeout(() => {
        setToasts((current) => current.filter((toast) => toast.id !== id));
      }, TOAST_EXIT_MS);
    }, 4000);
  };

  return (
    <ToastContext.Provider value={push}>
      {children}
      <div
        className={cx(
          "pointer-events-none fixed inset-x-0 bottom-0 z-[60] flex flex-col items-center gap-2 p-4 sm:items-end",
          // Task #351: fixed at the true bottom edge, so a toast can land
          // directly over the home-indicator area on iPhone X+ - this is
          // nonzero even in an ordinary browser tab (not just standalone
          // PWA mode), unlike the top inset. `max()` keeps the existing 1rem
          // (`p-4`) as a floor on devices with no inset.
          "supports-[padding:max(0px)]:pb-[max(1rem,env(safe-area-inset-bottom))]",
        )}
        // Polite: a toast confirms something the user just did, so it must not
        // interrupt whatever they are reading or typing next.
        aria-live="polite"
        aria-atomic="false"
      >
        {toasts.map((toast) => (
          <div
            key={toast.id}
            className={cx(
              "pointer-events-auto w-full max-w-sm transition duration-fast ease-out",
              toast.leaving ? "translate-y-1 opacity-0" : "animate-rise",
            )}
          >
            <div className="rounded-xl border border-line bg-surface shadow-lg">
              <Alert tone={toast.tone}>{toast.message}</Alert>
            </div>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

/** Returns a no-op outside a `ToastProvider` so a screen never crashes over a toast. */
export function useToast() {
  const push = useContext(ToastContext);
  return push ?? (() => {});
}
