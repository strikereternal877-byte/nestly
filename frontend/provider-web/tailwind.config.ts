import type { Config } from "tailwindcss";

/**
 * Every color here resolves to a CSS variable defined in src/app/globals.css,
 * wrapped so Tailwind's opacity modifiers still work (`bg-surface/60`).
 *
 * Dark mode is class-driven rather than media-driven: the inline script in
 * layout.tsx sets `.dark` on <html> before first paint from the stored
 * preference, falling back to the OS setting. That keeps the `dark:` variants
 * and the token overrides in globals.css agreeing with each other, and makes
 * an in-app theme toggle possible.
 */
const rgb = (variable: string) => `rgb(var(${variable}) / <alpha-value>)`;

const config: Config = {
  darkMode: "class",
  content: [
    "./src/pages/**/*.{js,ts,jsx,tsx,mdx}",
    "./src/components/**/*.{js,ts,jsx,tsx,mdx}",
    "./src/app/**/*.{js,ts,jsx,tsx,mdx}",
  ],
  theme: {
    extend: {
      colors: {
        brand: {
          50: rgb("--brand-50"),
          100: rgb("--brand-100"),
          200: rgb("--brand-200"),
          300: rgb("--brand-300"),
          400: rgb("--brand-400"),
          500: rgb("--brand-500"),
          600: rgb("--brand-600"),
          700: rgb("--brand-700"),
          800: rgb("--brand-800"),
          900: rgb("--brand-900"),
          950: rgb("--brand-950"),
        },
        accent: {
          100: rgb("--accent-100"),
          300: rgb("--accent-300"),
          400: rgb("--accent-400"),
          500: rgb("--accent-500"),
          600: rgb("--accent-600"),
          700: rgb("--accent-700"),
        },

        bg: rgb("--bg"),
        surface: {
          DEFAULT: rgb("--surface"),
          2: rgb("--surface-2"),
          3: rgb("--surface-3"),
        },
        overlay: rgb("--overlay"),

        fg: {
          DEFAULT: rgb("--fg"),
          muted: rgb("--fg-muted"),
          subtle: rgb("--fg-subtle"),
          "on-brand": rgb("--fg-on-brand"),
        },

        line: {
          DEFAULT: rgb("--border"),
          strong: rgb("--border-strong"),
        },

        success: { DEFAULT: rgb("--success"), soft: rgb("--success-soft") },
        warning: { DEFAULT: rgb("--warning"), soft: rgb("--warning-soft") },
        danger: { DEFAULT: rgb("--danger"), soft: rgb("--danger-soft") },
        info: { DEFAULT: rgb("--info"), soft: rgb("--info-soft") },

        // Kept so existing `bg-background`/`text-foreground` call sites keep
        // resolving while screens migrate to the semantic names above.
        background: rgb("--bg"),
        foreground: rgb("--fg"),
      },

      borderColor: {
        DEFAULT: rgb("--border"),
      },

      ringColor: {
        DEFAULT: rgb("--ring"),
      },

      fontFamily: {
        sans: ["var(--font-geist-sans)", "ui-sans-serif", "system-ui", "sans-serif"],
        mono: ["var(--font-geist-mono)", "ui-monospace", "monospace"],
      },

      fontSize: {
        // Display sizes carry their own tracking so headings never need a
        // hand-tuned `tracking-*` at the call site.
        "display-sm": ["1.875rem", { lineHeight: "1.15", letterSpacing: "-0.022em" }],
        "display-md": ["2.375rem", { lineHeight: "1.1", letterSpacing: "-0.026em" }],
        "display-lg": ["3rem", { lineHeight: "1.05", letterSpacing: "-0.03em" }],
        "display-xl": ["3.75rem", { lineHeight: "1", letterSpacing: "-0.034em" }],
      },

      borderRadius: {
        sm: "0.375rem",
        DEFAULT: "0.5rem",
        md: "0.625rem",
        lg: "0.75rem",
        xl: "1rem",
        "2xl": "1.25rem",
        "3xl": "1.5rem",
        "4xl": "2rem",
      },

      boxShadow: {
        xs: "var(--shadow-xs)",
        sm: "var(--shadow-sm)",
        DEFAULT: "var(--shadow-sm)",
        md: "var(--shadow-md)",
        lg: "var(--shadow-lg)",
        xl: "var(--shadow-xl)",
        brand: "var(--shadow-brand)",
      },

      transitionTimingFunction: {
        out: "var(--ease-out)",
        "in-out": "var(--ease-in-out)",
      },

      transitionDuration: {
        fast: "var(--dur-fast)",
        DEFAULT: "var(--dur)",
        slow: "var(--dur-slow)",
      },

      animation: {
        "fade-in": "nestly-fade-in var(--dur) var(--ease-out) both",
        rise: "nestly-rise var(--dur-slow) var(--ease-out) both",
        pop: "nestly-pop var(--dur) var(--ease-out) both",
        shimmer: "nestly-shimmer 1.6s linear infinite",
      },

      backgroundImage: {
        "brand-gradient":
          "linear-gradient(135deg, rgb(var(--brand-700)) 0%, rgb(var(--brand-500)) 55%, rgb(var(--brand-400)) 100%)",
        "skeleton-shimmer":
          "linear-gradient(90deg, transparent 0%, rgb(var(--fg) / 0.06) 50%, transparent 100%)",
      },
    },
  },
  plugins: [],
};
export default config;
