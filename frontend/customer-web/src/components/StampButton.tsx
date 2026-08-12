"use client";

import Link from "next/link";
import { motion } from "motion/react";
import type { MouseEventHandler, ReactNode } from "react";
import { cx } from "@/components/ui";

/**
 * THE SERVICE LEDGER's primary CTA: a red ink-stamp button (FIRST VIEWPOINT).
 * A quick scale-down + settle "stamp landing" on press, distinct from the
 * shared `Button`'s gentle `active:scale-[0.98]` — this is the one deliberate
 * flourish for the ledger's confirm/book actions, not spammed on every
 * control. Reduced-motion is handled once, product-wide, by the
 * `MotionConfig reducedMotion="user"` in app/providers.tsx (same as every
 * other `motion.*` gesture in the app — see components/motion.tsx), so this
 * needs no manual `prefers-reduced-motion` check of its own.
 */
const STAMP_SPRING = { type: "spring", stiffness: 700, damping: 20, mass: 0.7 } as const;

interface StampButtonProps {
  children: ReactNode;
  href?: string;
  onClick?: MouseEventHandler<HTMLButtonElement>;
  type?: "button" | "submit";
  disabled?: boolean;
  fullWidth?: boolean;
  className?: string;
}

const STAMP_CLASS =
  "inline-flex h-12 select-none items-center justify-center gap-2 rounded-md border-2 border-brand-800 bg-brand-600 px-6 font-display text-sm uppercase tracking-[0.08em] text-fg-on-brand shadow-brand transition-colors duration-fast ease-out hover:bg-brand-700 disabled:cursor-not-allowed disabled:opacity-55";

export function StampButton({
  children,
  href,
  onClick,
  type = "button",
  disabled,
  fullWidth,
  className,
}: StampButtonProps) {
  const inner = (
    <motion.span
      whileHover={disabled ? undefined : { scale: 1.015 }}
      whileTap={disabled ? undefined : { scale: 0.9, rotate: -2.5 }}
      transition={STAMP_SPRING}
      className={cx(STAMP_CLASS, fullWidth && "w-full", className)}
    >
      <StampIcon />
      {children}
    </motion.span>
  );

  if (href) {
    return (
      <Link href={href} className={cx("inline-flex", fullWidth && "w-full")}>
        {inner}
      </Link>
    );
  }

  return (
    <button
      type={type}
      onClick={onClick}
      disabled={disabled}
      className={cx("inline-flex", fullWidth && "w-full")}
    >
      {inner}
    </button>
  );
}

function StampIcon() {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.25"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-4 w-4 shrink-0"
      aria-hidden
    >
      <path d="m5 13 4 4L19 7" />
    </svg>
  );
}
