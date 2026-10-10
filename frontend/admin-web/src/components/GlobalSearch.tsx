"use client";

import { useRouter } from "next/navigation";
import { useEffect, useMemo, useRef, useState } from "react";
import { cx, EmptyState, Field, Modal } from "@/components/ui";
import { useResetOnChange } from "@/hooks/useResetOnChange";
import { buildSearchIndex, matchEntry, type SearchCategory, type SearchIndexEntry } from "@/lib/search-index";
import { useAdminClaims } from "@/lib/use-admin-claims";

/** Fixed display order for result groups - Pages before Settings, matching how often each is the thing an admin is hunting for. */
const CATEGORY_ORDER: readonly SearchCategory[] = ["Pages", "Settings"];

/** Keeps the palette responsive on a broad query ("a") without rendering hundreds of rows. */
const MAX_RESULTS_PER_CATEGORY = 8;

interface MatchedEntry {
  entry: SearchIndexEntry;
  /** See `EntryMatch.matchedKeyword` - shown as a "Matched: …" hint when set. */
  matchedKeyword: string | null;
}

interface GroupedResults {
  category: SearchCategory;
  entries: MatchedEntry[];
}

function groupResults(index: SearchIndexEntry[], query: string): GroupedResults[] {
  const matched = index
    .map((entry) => ({ entry, ...matchEntry(entry, query) }))
    .filter((result) => result.matched);
  return CATEGORY_ORDER.map((category) => ({
    category,
    entries: matched.filter((result) => result.entry.category === category).slice(0, MAX_RESULTS_PER_CATEGORY),
  })).filter((group) => group.entries.length > 0);
}

/**
 * Single global "find anything" entry point (task: admin-web global search):
 * reachable from every admin page via the header trigger below (and
 * Ctrl/Cmd+K), unlike `settings/page.tsx`'s own search box which only
 * filters that one page. Finds both admin pages/sub-pages and individual
 * settings fields/feature flags - see `lib/search-index.ts` for how the
 * index is built and gated by the signed-in admin's permissions.
 *
 * Reuses the `Modal` primitive for the overlay shell rather than a bespoke
 * command-palette component - this app has exactly one modal system, and a
 * search-as-you-type list inside it needs nothing `Modal` doesn't already
 * provide (focus trap, Escape-to-close, scroll lock).
 */
export function GlobalSearch() {
  const router = useRouter();
  const claims = useAdminClaims();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [activeIndex, setActiveIndex] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  const index = useMemo(() => buildSearchIndex(claims), [claims]);
  const groups = useMemo(() => groupResults(index, query), [index, query]);
  const flatResults = useMemo(() => groups.flatMap((group) => group.entries), [groups]);

  const close = () => {
    setOpen(false);
    setQuery("");
    setActiveIndex(0);
  };

  const navigateTo = (entry: SearchIndexEntry) => {
    close();
    router.push(entry.href);
  };

  // Reset the active row whenever the visible result set changes, so
  // pressing Enter always activates a row that is actually on screen.
  useResetOnChange([query], () => setActiveIndex(0));

  // Global Ctrl/Cmd+K shortcut to open the palette from anywhere in the
  // admin app. No existing keybinding system to conflict with - the only
  // other global `keydown` listeners in this shell (AuthenticatedLayout's
  // nav drawer, this Modal's own focus trap, AdminHeader's account menu) all
  // key off Escape/Tab, never "k" - and a modifier-qualified shortcut does
  // not collide with normal typing the way a bare "/" would in a text input.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        setOpen(true);
      }
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, []);

  // Autofocus the search field itself on open. Modal already focuses the
  // first focusable element on open (the input), so this is a no-op after
  // that pass most of the time - kept as a defensive extra `select()` pass
  // (of nothing, harmlessly) is not needed; a direct focus is cheap insurance
  // if the timing of Modal's own effect ever changes.
  useEffect(() => {
    if (open) inputRef.current?.focus();
  }, [open]);

  const handleKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      setActiveIndex((current) => Math.min(current + 1, flatResults.length - 1));
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      setActiveIndex((current) => Math.max(current - 1, 0));
    } else if (event.key === "Enter") {
      event.preventDefault();
      const result = flatResults[activeIndex];
      if (result) navigateTo(result.entry);
    }
  };

  const trimmedQuery = query.trim();

  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        aria-label="Search pages and settings"
        className="flex h-9 w-9 items-center justify-center rounded-lg text-fg-muted transition-colors duration-fast ease-out hover:bg-surface-3 hover:text-fg sm:w-56 sm:justify-start sm:gap-2 sm:border sm:border-line sm:px-3 sm:text-sm"
      >
        <svg
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
          className="h-[18px] w-[18px] shrink-0"
          aria-hidden
        >
          <circle cx="11" cy="11" r="7" />
          <path d="m21 21-4.3-4.3" />
        </svg>
        <span className="hidden text-fg-subtle sm:inline">Search…</span>
        <span className="ml-auto hidden rounded border border-line px-1.5 py-0.5 text-[0.6875rem] font-medium text-fg-subtle sm:inline">
          ⌘K
        </span>
      </button>

      <Modal open={open} onClose={close} title="Search" size="lg">
        <div className="flex flex-col gap-4">
          <Field
            ref={inputRef}
            type="search"
            label="Search pages and settings"
            placeholder="Find a page, feature or setting…"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            onKeyDown={handleKeyDown}
          />

          {trimmedQuery === "" ? (
            <p className="px-1 text-sm text-fg-muted">Start typing to search every page and setting you can access.</p>
          ) : flatResults.length === 0 ? (
            <EmptyState
              title="No results"
              description={`Nothing matched "${trimmedQuery}". Try a different search term.`}
            />
          ) : (
            <div className="flex flex-col gap-4">
              {groups.map((group) => (
                <div key={group.category}>
                  <p className="mb-1.5 px-1 text-[0.6875rem] font-semibold uppercase tracking-wider text-fg-subtle">
                    {group.category}
                  </p>
                  <div className="flex flex-col gap-0.5">
                    {group.entries.map((matchedEntry) => {
                      const { entry, matchedKeyword } = matchedEntry;
                      // flatResults is the same groups-then-entries flattening
                      // in the same order, so an entry's position there is
                      // exactly its on-screen row index - computed by lookup
                      // rather than a mutable counter incremented across this
                      // nested map (react-hooks/immutability).
                      const rowIndex = flatResults.indexOf(matchedEntry);
                      const isActive = rowIndex === activeIndex;
                      return (
                        <button
                          key={`${entry.category}-${entry.href}-${entry.label}`}
                          type="button"
                          onClick={() => navigateTo(entry)}
                          onMouseEnter={() => setActiveIndex(rowIndex)}
                          className={cx(
                            "flex flex-col items-start gap-0.5 rounded-lg px-3 py-2 text-left transition-colors duration-fast ease-out",
                            isActive ? "bg-brand-50 dark:bg-brand-500/15" : "hover:bg-surface-2",
                          )}
                        >
                          <span
                            className={cx(
                              "text-sm font-medium",
                              isActive ? "text-brand-700 dark:text-brand-300" : "text-fg",
                            )}
                          >
                            {entry.label}
                          </span>
                          {entry.description ? (
                            <span className="line-clamp-1 text-xs text-fg-muted">{entry.description}</span>
                          ) : null}
                          {/* The query didn't match this entry's own label/description at all -
                              it matched a hidden field (e.g. "Slot rules" for "book", via its
                              "Max advance booking (days)" field). Without this, that result looks
                              unrelated to the query rather than correct. */}
                          {matchedKeyword ? (
                            <span className="line-clamp-1 text-xs text-fg-subtle">
                              Matched: <span className="text-fg-muted">{matchedKeyword}</span>
                            </span>
                          ) : null}
                        </button>
                      );
                    })}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </Modal>
    </>
  );
}
