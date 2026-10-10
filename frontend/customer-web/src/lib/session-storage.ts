/**
 * Where the signed-in session is kept.
 *
 * In a normal browser tab it stays in sessionStorage: the session dies with the tab, which narrows the window for
 * anyone who gets a script onto the page. An app installed to a phone's home screen is different. There is no tab to
 * keep open: closing or swiping the app away ends its sessionStorage, which would sign the customer out every time they
 * open it. An installed app is meant to behave like any other app on the phone and stay signed in, so there the session
 * is kept in localStorage.
 *
 * No imports and no direct use of `window`, so the choice can be unit tested with plain fakes (`npm run test:unit`).
 * `browserStorageEnvironment` is the one browser-bound piece and is only called from auth.ts.
 */

export interface KeyValueStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export interface StorageEnvironment {
  /** Lives as long as the browser tab. */
  tab: KeyValueStorage;
  /** Survives the app being closed. */
  device: KeyValueStorage;
  /** Running as an app installed to the home screen, not in a browser tab. */
  installed: boolean;
}

/**
 * Whether the page is running as an installed app. Chromium reports it through the `display-mode` media query; iOS
 * Safari only through its own `navigator.standalone`.
 */
export function isInstalledDisplayMode(standaloneMediaMatches: boolean, iosStandalone: boolean | undefined): boolean {
  return standaloneMediaMatches || iosStandalone === true;
}

/** The store the session is read from and written to right now. */
export function sessionStoreFor(environment: StorageEnvironment): KeyValueStorage {
  return environment.installed ? environment.device : environment.tab;
}

/**
 * Signing out removes the session from both stores. A browser tab and the installed app can share a device, and a
 * sign-out in either one must not leave a usable session behind in the other.
 */
export function removeFromBothStores(environment: StorageEnvironment, keys: readonly string[]): void {
  for (const key of keys) {
    environment.tab.removeItem(key);
    environment.device.removeItem(key);
  }
}

/** The live browser's storages and display mode. Call from client code only. */
export function browserStorageEnvironment(): StorageEnvironment {
  return {
    tab: window.sessionStorage,
    device: window.localStorage,
    installed: isInstalledDisplayMode(
      window.matchMedia("(display-mode: standalone)").matches,
      (window.navigator as Navigator & { standalone?: boolean }).standalone,
    ),
  };
}
