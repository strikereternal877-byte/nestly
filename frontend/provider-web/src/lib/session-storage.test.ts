import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { isInstalledDisplayMode, removeFromBothStores, sessionStoreFor } from "./session-storage.ts";
import type { KeyValueStorage, StorageEnvironment } from "./session-storage.ts";

/**
 * A customer or provider who installs the app must stay signed in when they close it and come back; a browser tab keeps
 * the stricter "session dies with the tab" behaviour. Driven with fake storages: a tab's storage is emptied when the tab
 * (or the installed app's window) closes, the device's is not.
 */

function fakeStorage(): KeyValueStorage & { size: number } {
  const values = new Map<string, string>();
  return {
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => void values.set(key, value),
    removeItem: (key) => void values.delete(key),
    get size() {
      return values.size;
    },
  };
}

const KEYS = ["access", "refresh", "expires"] as const;

function signIn(environment: StorageEnvironment): void {
  const store = sessionStoreFor(environment);
  for (const key of KEYS) store.setItem(key, `${key}-token`);
}

function isSignedIn(environment: StorageEnvironment): boolean {
  return sessionStoreFor(environment).getItem("access") !== null;
}

describe("isInstalledDisplayMode", () => {
  it("is an installed app when the display-mode query says standalone (Android, desktop)", () => {
    assert.equal(isInstalledDisplayMode(true, undefined), true);
  });

  it("is an installed app when iOS reports navigator.standalone", () => {
    assert.equal(isInstalledDisplayMode(false, true), true);
  });

  it("is a browser tab otherwise, including when iOS reports standalone as false", () => {
    assert.equal(isInstalledDisplayMode(false, undefined), false);
    assert.equal(isInstalledDisplayMode(false, false), false);
  });
});

describe("sessionStoreFor", () => {
  it("uses the tab's storage in a browser and the device's in an installed app", () => {
    const tab = fakeStorage();
    const device = fakeStorage();

    assert.equal(sessionStoreFor({ tab, device, installed: false }), tab);
    assert.equal(sessionStoreFor({ tab, device, installed: true }), device);
  });
});

describe("staying signed in", () => {
  it("an installed app is still signed in after it is closed and opened again", () => {
    const device = fakeStorage();
    signIn({ tab: fakeStorage(), device, installed: true });

    const reopened: StorageEnvironment = { tab: fakeStorage(), device, installed: true };

    assert.equal(isSignedIn(reopened), true);
    assert.equal(sessionStoreFor(reopened).getItem("refresh"), "refresh-token");
  });

  it("a browser tab is signed out once the tab is closed, as before", () => {
    const device = fakeStorage();
    signIn({ tab: fakeStorage(), device, installed: false });

    const newTab: StorageEnvironment = { tab: fakeStorage(), device, installed: false };

    assert.equal(isSignedIn(newTab), false);
    assert.equal(device.size, 0, "a browser sign-in never lands in the long-lived store");
  });

  it("the installed app does not pick up a browser tab's session, or the other way round", () => {
    const tab = fakeStorage();
    const device = fakeStorage();
    signIn({ tab, device, installed: false });

    assert.equal(isSignedIn({ tab, device, installed: true }), false);

    signIn({ tab: fakeStorage(), device, installed: true });
    assert.equal(isSignedIn({ tab: fakeStorage(), device, installed: false }), false);
  });
});

describe("removeFromBothStores", () => {
  it("signing out removes the session wherever it was kept and leaves unrelated entries alone", () => {
    const tab = fakeStorage();
    const device = fakeStorage();
    for (const key of KEYS) {
      tab.setItem(key, "x");
      device.setItem(key, "x");
    }
    device.setItem("theme", "dark");

    removeFromBothStores({ tab, device, installed: true }, KEYS);

    assert.equal(tab.size, 0);
    assert.equal(device.size, 1);
    assert.equal(device.getItem("theme"), "dark");
  });
});
