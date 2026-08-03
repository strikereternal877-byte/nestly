/**
 * Theme preference, persisted per browser.
 *
 * Tailwind's `dark:` variant is class-driven here (see tailwind.config.ts), so
 * something has to put `.dark` on <html>. Doing that from a React effect would
 * paint the light theme first and then flip — the classic dark-mode flash — so
 * `THEME_INIT_SCRIPT` runs synchronously in <head> before the body renders and
 * this module only handles changes made after that.
 */

export type ThemePreference = "light" | "dark" | "system";

export const THEME_STORAGE_KEY = "nestly.theme";

/**
 * Inlined into <head> as a blocking script. Deliberately dependency-free,
 * minified by hand, and wrapped in try/catch: Safari's private mode throws on
 * localStorage access, and a theme preference is never worth a blank page.
 */
export const THEME_INIT_SCRIPT = `(function(){try{var p=localStorage.getItem("${THEME_STORAGE_KEY}");var d=p==="dark"||((!p||p==="system")&&window.matchMedia("(prefers-color-scheme: dark)").matches);document.documentElement.classList.toggle("dark",d);document.documentElement.style.colorScheme=d?"dark":"light";}catch(e){}})();`;

function isPreference(value: unknown): value is ThemePreference {
  return value === "light" || value === "dark" || value === "system";
}

export function getThemePreference(): ThemePreference {
  if (typeof window === "undefined") return "system";
  try {
    const stored = window.localStorage.getItem(THEME_STORAGE_KEY);
    return isPreference(stored) ? stored : "system";
  } catch {
    return "system";
  }
}

/** The theme actually on screen, once "system" has been resolved. */
export function resolveTheme(preference: ThemePreference): "light" | "dark" {
  if (preference !== "system") return preference;
  if (typeof window === "undefined") return "light";
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

export function setThemePreference(preference: ThemePreference): void {
  if (typeof window === "undefined") return;

  const resolved = resolveTheme(preference);
  document.documentElement.classList.toggle("dark", resolved === "dark");
  // Keeps native controls (scrollbars, date pickers, autofill) in step with
  // the app's own surfaces — CSS alone cannot restyle those.
  document.documentElement.style.colorScheme = resolved;

  try {
    window.localStorage.setItem(THEME_STORAGE_KEY, preference);
  } catch {
    // Storage unavailable: the theme still applies for this page view.
  }

  window.dispatchEvent(new Event("nestly:themechange"));
}

/**
 * Notifies on both in-app changes and OS-level changes, so a window left on
 * "system" follows the OS switching to dark at sunset without a reload.
 */
export function subscribeToTheme(onChange: () => void): () => void {
  if (typeof window === "undefined") return () => {};

  const media = window.matchMedia("(prefers-color-scheme: dark)");
  const handleSystemChange = () => {
    if (getThemePreference() === "system") {
      setThemePreference("system");
    }
    onChange();
  };

  window.addEventListener("nestly:themechange", onChange);
  // Fires when another tab changes the preference.
  window.addEventListener("storage", onChange);
  media.addEventListener("change", handleSystemChange);

  return () => {
    window.removeEventListener("nestly:themechange", onChange);
    window.removeEventListener("storage", onChange);
    media.removeEventListener("change", handleSystemChange);
  };
}
