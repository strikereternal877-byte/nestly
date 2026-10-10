import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  GEOLOCATION_PERMISSION_DENIED,
  GEOLOCATION_TIMEOUT,
  ensureLocationPermission,
  isPermissionDenied,
  locateCustomer,
} from "./geolocation";
import type { GeolocationEnvironment, LocatePhase } from "./geolocation";

/**
 * "Allow location" must work in one tap. These tests drive lib/geolocation.ts with a fake browser so each way the
 * browser's permission question and the first fix can interleave is covered without a phone: permission already
 * granted, a prompt still open (the browser failing the call at once, or the call succeeding), a real denial, a
 * prompt nobody answers, and a location provider that is not ready yet.
 *
 * Run with `npm run test:unit`.
 */

const POSITION = { coords: { latitude: 26.9124, longitude: 75.7873, accuracy: 20 }, timestamp: 0 } as unknown as GeolocationPosition;

/** What the next `getCurrentPosition` call does: succeed, fail with a code, or never answer (a prompt that is still open). */
type Step = "ok" | "pending" | { error: number };

class FakeStatus extends EventTarget {
  constructor(public state: PermissionState) {
    super();
  }

  /** The customer answers (or changes) the permission: the state moves and `change` fires, as in a browser. */
  answer(next: PermissionState): void {
    this.state = next;
    this.dispatchEvent(new Event("change"));
  }
}

interface CallRecord {
  highAccuracy: boolean | undefined;
  timeout: number | undefined;
}

function fakeBrowser(options: { permission: FakeStatus | "unsupported"; steps: Step[] }) {
  const calls: CallRecord[] = [];
  const steps = [...options.steps];

  const environment: GeolocationEnvironment = {
    geolocation: {
      getCurrentPosition: (success, error, positionOptions) => {
        calls.push({ highAccuracy: positionOptions?.enableHighAccuracy, timeout: positionOptions?.timeout });
        const step = steps.shift() ?? "pending";
        if (step === "pending") return;
        queueMicrotask(() => {
          if (step === "ok") {
            success(POSITION);
          } else {
            error?.({ code: step.error, message: "fake" } as GeolocationPositionError);
          }
        });
      },
    },
    permissions:
      options.permission === "unsupported"
        ? undefined
        : { query: async () => options.permission as unknown as PermissionStatus },
  };

  return { environment, calls };
}

const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 15));
const NO_DELAY = { retryDelaysMs: [0, 0] } as const;

describe("locateCustomer", () => {
  it("permission already granted: one request, no waiting phase", async () => {
    const { environment, calls } = fakeBrowser({ permission: new FakeStatus("granted"), steps: ["ok"] });
    const phases: LocatePhase[] = [];

    const position = await locateCustomer(environment, { ...NO_DELAY, onPhase: (p) => phases.push(p) });

    assert.equal(position, POSITION);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].highAccuracy, true);
    assert.deepEqual(phases, ["locating"]);
  });

  it("permission denied before the tap: fails at once as a real denial and never asks the browser", async () => {
    const { environment, calls } = fakeBrowser({ permission: new FakeStatus("denied"), steps: ["ok"] });

    await assert.rejects(locateCustomer(environment, NO_DELAY), (error) => isPermissionDenied(error));
    assert.equal(calls.length, 0);
  });

  it("prompt open and the browser fails the call at once: waits, and carries on the moment the customer allows (one tap)", async () => {
    const status = new FakeStatus("prompt");
    // 1st call = the one that raises the prompt (fails at once, state stays "prompt"); 2nd = the real position request.
    const { environment, calls } = fakeBrowser({ permission: status, steps: [{ error: GEOLOCATION_PERMISSION_DENIED }, "ok"] });
    const phases: LocatePhase[] = [];

    const pending = locateCustomer(environment, { ...NO_DELAY, onPhase: (p) => phases.push(p) });
    await settle();
    assert.deepEqual(phases, ["awaiting-permission"], "still waiting for the customer, not failed");
    assert.equal(calls.length, 1, "no position request is made while the question is open");

    status.answer("granted");

    assert.equal(await pending, POSITION);
    assert.deepEqual(phases, ["awaiting-permission", "locating"]);
    assert.equal(calls.length, 2);
    assert.equal(calls[1].highAccuracy, true);
  });

  it("prompt open and the call just waits for the customer: no short timer runs against them, and a grant carries on", async () => {
    const status = new FakeStatus("prompt");
    const { environment, calls } = fakeBrowser({ permission: status, steps: ["pending", "ok"] });

    const pending = locateCustomer(environment, NO_DELAY);
    await settle();
    // The call that raises the prompt carries the long wait, not the 8s fix budget.
    assert.ok((calls[0].timeout ?? 0) >= 60_000);

    status.answer("granted");
    assert.equal(await pending, POSITION);
  });

  it("prompt open and the raising call itself returns a position: resolves without needing a change event", async () => {
    const { environment } = fakeBrowser({ permission: new FakeStatus("prompt"), steps: ["ok", "ok"] });

    assert.equal(await locateCustomer(environment, NO_DELAY), POSITION);
  });

  it("prompt answered with a refusal: stops as a real denial and never requests a position", async () => {
    const status = new FakeStatus("prompt");
    const { environment, calls } = fakeBrowser({ permission: status, steps: [{ error: GEOLOCATION_PERMISSION_DENIED }, "ok"] });

    const pending = locateCustomer(environment, NO_DELAY);
    await settle();
    status.answer("denied");

    await assert.rejects(pending, (error) => isPermissionDenied(error));
    assert.equal(calls.length, 1);
  });

  it("a refusal reported only through the failed call (no change event) is still noticed", async () => {
    const status = new FakeStatus("prompt");
    const { environment } = fakeBrowser({ permission: status, steps: [{ error: GEOLOCATION_PERMISSION_DENIED }, "ok"] });

    const pending = locateCustomer(environment, NO_DELAY);
    await settle();
    status.state = "denied"; // the state moves but this browser fires no event
    // The next signal that comes along - here the raising call's own error arriving later - re-reads the state.
    await assert.rejects(
      ensureLocationPermission(
        { geolocation: { getCurrentPosition: (_s, error) => error?.({ code: 1, message: "x" } as GeolocationPositionError) }, permissions: { query: async () => status as unknown as PermissionStatus } },
      ),
      (error) => isPermissionDenied(error),
    );
    status.answer("granted"); // let the first pending call finish so the test does not leak a timer
    await pending;
  });

  it("prompt nobody answers: gives up with a timeout, not a refusal", async () => {
    const { environment } = fakeBrowser({ permission: new FakeStatus("prompt"), steps: ["pending"] });

    await assert.rejects(locateCustomer(environment, { ...NO_DELAY, permissionWaitMs: 25 }), (error) => {
      assert.equal((error as { code: number }).code, GEOLOCATION_TIMEOUT);
      return !isPermissionDenied(error);
    });
  });

  it("no Permissions API: a location provider that is not ready yet is retried after a pause and then succeeds", async () => {
    // Round 1: high-accuracy and coarse both unavailable. Round 2: high-accuracy unavailable, coarse ok.
    const { environment, calls } = fakeBrowser({ permission: "unsupported", steps: [{ error: 2 }, { error: 2 }, { error: 2 }, "ok"] });

    assert.equal(await locateCustomer(environment, NO_DELAY), POSITION);
    assert.equal(calls.length, 4);
    assert.equal(calls[3].highAccuracy, false, "the second round fell back to the coarse attempt");
  });

  it("no Permissions API: an explicit denial is not retried", async () => {
    const { environment, calls } = fakeBrowser({ permission: "unsupported", steps: [{ error: GEOLOCATION_PERMISSION_DENIED }, "ok"] });

    await assert.rejects(locateCustomer(environment, NO_DELAY), (error) => isPermissionDenied(error));
    assert.equal(calls.length, 1);
  });

  it("every round fails: rejects with the last error after three rounds, not for ever", async () => {
    const failures: Step[] = Array.from({ length: 12 }, () => ({ error: 2 }));
    const { environment, calls } = fakeBrowser({ permission: new FakeStatus("granted"), steps: failures });

    await assert.rejects(locateCustomer(environment, NO_DELAY), (error) => (error as { code: number }).code === 2);
    assert.equal(calls.length, 6, "three rounds of high-accuracy then coarse");
  });
});
