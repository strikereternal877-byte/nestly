"use client";

import { createContext, forwardRef, useContext, useEffect, useId, useRef, useState } from "react";
import type {
  ButtonHTMLAttributes,
  InputHTMLAttributes,
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
 * This file is replicated verbatim across customer-web, admin-web and
 * provider-web (the three apps are independent Next projects with no shared
 * package), so it is a deliberate superset: an app that never renders a
 * `Table` still ships the identical file, and the three stay in step by being
 * literally the same bytes. Changing it in one app means porting to all three.
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
        "w-full overflow-hidden rounded-2xl border border-line bg-surface shadow-sm",
        className,
      )}
    >
      {hasHeader ? (
        <div className="flex items-start justify-between gap-4 px-6 pt-6">
          <div className="min-w-0">
            {title ? <h2 className="text-base font-semibold text-fg">{title}</h2> : null}
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
  neutral: "bg-surface-3 text-fg-muted ring-line",
  brand: "bg-brand-50 text-brand-700 ring-brand-600/20 dark:bg-brand-500/15 dark:text-brand-300",
  success: "bg-success-soft text-success ring-success/20",
  warning: "bg-warning-soft text-warning ring-warning/20",
  danger: "bg-danger-soft text-danger ring-danger/20",
  info: "bg-info-soft text-info ring-info/20",
  accent: "bg-accent-100 text-accent-700 ring-accent-600/20 dark:bg-accent-500/15 dark:text-accent-300",
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
        "inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-medium ring-1 ring-inset",
        BADGE_TONES[tone],
        className,
      )}
    >
      {children}
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
 * system. The focus treatment is a ring plus a border shift rather than the
 * browser default outline, and invalid controls carry it in the danger tone.
 */
const CONTROL_BASE =
  "w-full rounded-lg border bg-surface px-3 py-2 text-sm text-fg shadow-xs outline-none transition duration-fast ease-out placeholder:text-fg-subtle disabled:cursor-not-allowed disabled:bg-surface-2 disabled:text-fg-subtle";
const CONTROL_IDLE =
  "border-line hover:border-line-strong focus:border-brand-600 focus:ring-2 focus:ring-brand-600/25";
const CONTROL_INVALID = "border-danger focus:border-danger focus:ring-2 focus:ring-danger/25";

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
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium text-fg">
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

/** Derives a stable id from the field name so label/control always agree. */
function controlId(explicit: string | undefined, name: string | undefined, label: string): string {
  return explicit ?? `field-${name ?? label.toLowerCase().replace(/\s+/g, "-")}`;
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
}

export const Field = forwardRef<HTMLInputElement, FieldProps>(function Field(
  { label, error, hint, leading, id, className = "", ...props },
  ref,
) {
  const inputId = controlId(id, props.name, label);

  const input = (
    <input
      {...props}
      id={inputId}
      ref={ref}
      aria-invalid={error ? true : undefined}
      aria-describedby={error ? `${inputId}-error` : hint ? `${inputId}-hint` : undefined}
      className={cx(
        CONTROL_BASE,
        error ? CONTROL_INVALID : CONTROL_IDLE,
        Boolean(leading) && "pl-9",
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
      {leading ? (
        <div className="relative">
          <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-sm text-fg-subtle">
            {leading}
          </span>
          {input}
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
  const textareaId = controlId(id, props.name, label);

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
  const selectId = controlId(id, props.name, label);

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

export function Checkbox({
  label,
  ...props
}: InputHTMLAttributes<HTMLInputElement> & { label: string }) {
  const inputId = controlId(props.id, props.name, `checkbox-${label}`);
  return (
    <label htmlFor={inputId} className="flex cursor-pointer items-center gap-2.5 text-sm text-fg">
      <input
        {...props}
        id={inputId}
        type="checkbox"
        className="h-4 w-4 shrink-0 cursor-pointer rounded border-line-strong text-brand-600 accent-brand-600"
      />
      {label}
    </label>
  );
}

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

const BUTTON_VARIANTS = {
  primary:
    "bg-brand-600 text-fg-on-brand shadow-brand hover:bg-brand-700 active:bg-brand-800 disabled:shadow-none",
  secondary:
    "border border-line bg-surface text-fg shadow-xs hover:border-line-strong hover:bg-surface-2 active:bg-surface-3",
  danger:
    "bg-danger text-white shadow-xs hover:brightness-95 active:brightness-90 dark:text-bg",
  ghost: "text-fg-muted hover:bg-surface-3 hover:text-fg active:bg-surface-3",
  subtle:
    "bg-brand-50 text-brand-700 hover:bg-brand-100 dark:bg-brand-500/15 dark:text-brand-300 dark:hover:bg-brand-500/25",
  link: "text-brand-600 underline-offset-4 hover:underline dark:text-brand-400",
} as const;

const BUTTON_SIZES = {
  sm: "h-8 gap-1.5 px-3 text-xs",
  md: "h-10 gap-2 px-4 text-sm",
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
        "inline-flex select-none items-center justify-center whitespace-nowrap rounded-lg font-medium transition duration-fast ease-out",
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
        "inline-flex h-9 w-9 items-center justify-center rounded-lg transition duration-fast ease-out disabled:cursor-not-allowed disabled:opacity-55",
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
    <header className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
      <div className="min-w-0">
        {breadcrumbs ? <div className="mb-2">{breadcrumbs}</div> : null}
        <h1 className="text-display-sm font-semibold text-fg">{title}</h1>
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
}: {
  label: string;
  value: string;
  title?: string;
  hint?: string;
  delta?: { value: string; direction: "up" | "down" };
}) {
  return (
    <div className="rounded-2xl border border-line bg-surface p-5 shadow-sm">
      <p className="text-sm font-medium text-fg-muted">{label}</p>
      <div className="mt-2 flex items-baseline gap-2">
        <p className="nums text-3xl font-semibold text-fg" title={title}>
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
      {hint ? <p className="mt-1 text-xs text-fg-subtle">{hint}</p> : null}
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
    <thead className="border-b border-line bg-surface-2 text-left">
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
        "whitespace-nowrap px-4 py-3 text-xs font-semibold uppercase tracking-wide text-fg-muted",
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
              "relative whitespace-nowrap px-4 py-2.5 text-sm font-medium transition-colors duration-fast ease-out",
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
        onClose();
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
  }, [open, onClose]);

  if (!open) return null;

  const sizes = { sm: "max-w-sm", md: "max-w-lg", lg: "max-w-2xl" } as const;

  return (
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
          "relative w-full animate-pop rounded-t-2xl border border-line bg-surface shadow-xl outline-none sm:rounded-2xl",
          sizes[size],
        )}
      >
        <div className="flex items-start justify-between gap-4 px-6 pt-6">
          <div className="min-w-0">
            <h2 id={titleId} className="text-base font-semibold text-fg">
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
    </div>
  );
}

/* -------------------------------------------------------------------------- */
/* Toast                                                                      */
/* -------------------------------------------------------------------------- */

interface Toast {
  id: number;
  tone: keyof typeof ALERT_TONES;
  message: string;
}

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
    setToasts((current) => [...current, { id, tone, message }]);
    window.setTimeout(() => {
      setToasts((current) => current.filter((toast) => toast.id !== id));
    }, 4000);
  };

  return (
    <ToastContext.Provider value={push}>
      {children}
      <div
        className="pointer-events-none fixed inset-x-0 bottom-0 z-[60] flex flex-col items-center gap-2 p-4 sm:items-end"
        // Polite: a toast confirms something the user just did, so it must not
        // interrupt whatever they are reading or typing next.
        aria-live="polite"
        aria-atomic="false"
      >
        {toasts.map((toast) => (
          <div key={toast.id} className="pointer-events-auto w-full max-w-sm animate-rise">
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
