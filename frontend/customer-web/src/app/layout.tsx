import type { Metadata } from "next";
import localFont from "next/font/local";
import { Archivo, Special_Elite } from "next/font/google";
import { SiteHeader } from "@/components/SiteHeader";
import { ToastProvider } from "@/components/ui";
import { THEME_INIT_SCRIPT } from "@/lib/theme";
import { Providers } from "./providers";
import "./globals.css";

// THE SERVICE LEDGER (candidate 7/7, seed 394fa208): "Archivo", a workhorse
// grotesk with a document/form character, replaces Jost as the product's
// body typeface — kept under the same `--font-geist-sans` CSS variable name
// so tailwind.config.ts's `fontFamily.sans` and every existing call site
// keep resolving without a second change.
const archivo = Archivo({
  subsets: ["latin"],
  weight: ["400", "500", "600", "700"],
  variable: "--font-geist-sans",
  display: "swap",
});
// "Special Elite" — authentic vintage typewriter/stamp character — is the
// ledger's display face: headlines, category tab labels, stamped callouts.
// A new `--font-display` variable / `fontFamily.display` Tailwind key,
// deliberately not reusing `--font-geist-sans` since it is not a body face.
const specialElite = Special_Elite({
  subsets: ["latin"],
  weight: ["400"],
  variable: "--font-display",
  display: "swap",
});
const geistMono = localFont({
  src: "./fonts/GeistMonoVF.woff",
  variable: "--font-geist-mono",
  weight: "100 900",
});

export const metadata: Metadata = {
  title: {
    default: "Nestly — Trusted home services, booked in minutes",
    template: "%s · Nestly",
  },
  description:
    "Cleaning, repairs, salon and more — vetted professionals, upfront pricing, and slots that fit your day.",
};

/** Paints the browser chrome to match the theme on each side of the switch. */
export const viewport = {
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: "#d9c39a" },
    { media: "(prefers-color-scheme: dark)", color: "#211c16" },
  ],
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    // suppressHydrationWarning: the pre-paint script below mutates <html>'s
    // class and style before React hydrates, which React would otherwise flag
    // as a server/client mismatch on this element.
    <html lang="en" suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: THEME_INIT_SCRIPT }} />
      </head>
      <body
        className={`${archivo.variable} ${specialElite.variable} ${geistMono.variable} antialiased`}
      >
        {/*
THESIS: The service ledger you'd actually keep — not another gradient hero.
OWN-WORLD: kraft/manila ground, ink-blue text, stamped rust-red confirmation ink, per-category tab colors like binder dividers, typewriter/stamp display type + grotesk body + mono ledger numerals.
STORY: visitor sees their home's service record already exists — pick a tab (category), see upfront-priced ledger rows, book a slot, recurring visits stamp the record automatically.
FIRST VIEWPOINT: full-bleed kraft ledger card floating on dark ink ground, category tabs across the top edge like real dividers, one open "page" showing a real booking row mid-motion (a stamp landing), primary CTA as a red ink-stamp button.
FORM: Service Ledger, candidate 7/7, seed key 394fa208.
FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, and DESIGN.md.
        */}
        <Providers>
          <ToastProvider>
            {/* Lets keyboard and screen-reader users jump the nav on every page. */}
            <a
              href="#main"
              className="sr-only focus:not-sr-only focus:absolute focus:left-4 focus:top-4 focus:z-[70] focus:rounded-lg focus:bg-brand-600 focus:px-4 focus:py-2 focus:text-sm focus:font-medium focus:text-fg-on-brand"
            >
              Skip to content
            </a>
            <SiteHeader />
            {/* Skip-link target. A wrapper rather than the pages' own <main>
                elements, so the anchor works without editing every route. */}
            <div id="main">{children}</div>
          </ToastProvider>
        </Providers>
      </body>
    </html>
  );
}
