import { useState } from "react";

/**
 * Runs `reset` when any of `deps` differs from its value on the previous
 * render - React's own documented pattern for "adjusting state when a prop
 * changes" (https://react.dev/learn/you-might-not-need-an-effect#adjusting-some-state-when-a-prop-changes),
 * extracted here because the same shape - reset a list's page to 1 whenever
 * its filters change - is repeated near-identically across most of
 * admin-web's list pages.
 *
 * Deliberately `useState`, not `useRef`, to track the previous deps:
 * `eslint-plugin-react-hooks`'s `react-hooks/refs` rule forbids reading or
 * writing a ref's `.current` during render (refs don't participate in
 * React's render-phase update handling, so doing so can silently miss a
 * re-render). `useState`'s setter is exactly the primitive React documents
 * as safe to call conditionally during render - it restarts the render with
 * the new state immediately, rather than committing the stale one first the
 * way a `useEffect`-based reset would (which `react-hooks/set-state-in-effect`
 * flags this hook exists to avoid in the first place).
 */
export function useResetOnChange(deps: readonly unknown[], reset: () => void): void {
  const [prevDeps, setPrevDeps] = useState(deps);

  const changed = deps.length !== prevDeps.length || deps.some((dep, i) => dep !== prevDeps[i]);

  if (changed) {
    setPrevDeps(deps);
    reset();
  }
}
