"use client";

import { motion } from "motion/react";
import type { ReactNode } from "react";
import { Reveal, revealItem, SPRING } from "@/components/motion";

/** Trust markers / benefits (SRS 11.1.2). Static content - no backend-driven ratings/testimonials exist yet. */
export function TrustMarkers() {
  const markers: { icon: ReactNode; title: string; detail: string }[] = [
    {
      icon: <ShieldIcon />,
      title: "Verified professionals",
      detail: "Background-checked, trained, and rated by customers like you.",
    },
    {
      icon: <TagIcon />,
      title: "Upfront pricing",
      detail: "See the full price before you book — no surprises at the door.",
    },
    {
      icon: <RepeatIcon />,
      title: "Easy rescheduling",
      detail: "Plans changed? Move your slot in a couple of taps.",
    },
    {
      icon: <LifebuoyIcon />,
      title: "Support that answers",
      detail: "Real help from a real person if anything goes wrong.",
    },
  ];

  return (
    // A plain grid, not a labelled section: the page that renders this already
    // provides the "Why book with Nestly" heading, and repeating it here would
    // announce the same landmark name twice.
    <Reveal className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
      {markers.map((marker) => (
        <motion.div
          key={marker.title}
          variants={revealItem}
          whileHover={{ y: -4 }}
          transition={SPRING}
          className="rounded-lg border-2 border-line bg-surface p-5 shadow-xs transition-shadow duration-200 ease-out hover:border-line-strong hover:shadow-md"
        >
          <span
            aria-hidden="true"
            className="flex h-10 w-10 items-center justify-center rounded-md bg-brand-50 text-brand-600 dark:bg-brand-500/15 dark:text-brand-400"
          >
            {marker.icon}
          </span>
          <p className="mt-3.5 text-sm font-semibold text-fg">{marker.title}</p>
          <p className="mt-1 text-sm leading-relaxed text-fg-muted">{marker.detail}</p>
        </motion.div>
      ))}
    </Reveal>
  );
}

/* Line icons rather than emoji: emoji render in the platform's own colour and
   style, which is exactly the inconsistency a design system exists to remove. */

const ICON_PROPS = {
  viewBox: "0 0 24 24",
  fill: "none",
  stroke: "currentColor",
  strokeWidth: "1.75",
  strokeLinecap: "round",
  strokeLinejoin: "round",
  className: "h-5 w-5",
  "aria-hidden": true,
} as const;

function ShieldIcon() {
  return (
    <svg {...ICON_PROPS}>
      <path d="M12 3l7 3v5.5c0 4.2-3 8-7 9.5-4-1.5-7-5.3-7-9.5V6l7-3Z" />
      <path d="m9 12 2 2 4-4" />
    </svg>
  );
}

function TagIcon() {
  return (
    <svg {...ICON_PROPS}>
      <path d="M3 12.5V5a2 2 0 0 1 2-2h7.5L21 11.5 12.5 20 3 12.5Z" />
      <circle cx="8" cy="8" r="1.25" />
    </svg>
  );
}

function RepeatIcon() {
  return (
    <svg {...ICON_PROPS}>
      <path d="M4 9a5 5 0 0 1 5-5h8l-2.5-2.5M20 15a5 5 0 0 1-5 5H7l2.5 2.5" />
    </svg>
  );
}

function LifebuoyIcon() {
  return (
    <svg {...ICON_PROPS}>
      <circle cx="12" cy="12" r="9" />
      <circle cx="12" cy="12" r="3.5" />
      <path d="m5.7 5.7 3.8 3.8M14.5 14.5l3.8 3.8M18.3 5.7l-3.8 3.8M9.5 14.5l-3.8 3.8" />
    </svg>
  );
}
