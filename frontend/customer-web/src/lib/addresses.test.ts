import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { preferredAddress, resolveAddress } from "./addresses";
import type { CustomerAddress } from "./types";

const address = (id: string, pincodeId: string, isDefault = false) =>
  ({ id, pincodeId, isDefault }) as CustomerAddress;

const elsewhereDefault = address("bengaluru", "560034", true);
const jaipurHome = address("jaipur-home", "302033");
const jaipurOffice = address("jaipur-office", "302033");
const inJaipur = (a: CustomerAddress) => a.pincodeId === "302033";

describe("preferredAddress", () => {
  it("picks the default address only when it is in the area", () => {
    assert.equal(preferredAddress([elsewhereDefault, jaipurHome], inJaipur), jaipurHome);
    const jaipurDefault = address("jaipur-default", "302033", true);
    assert.equal(preferredAddress([jaipurHome, jaipurDefault], inJaipur), jaipurDefault);
  });

  it("never picks an address outside the area", () => {
    assert.equal(preferredAddress([elsewhereDefault], inJaipur), null);
  });
});

describe("resolveAddress", () => {
  const all = [elsewhereDefault, jaipurHome, jaipurOffice];

  it("keeps the customer's pick inside the area", () => {
    assert.equal(resolveAddress(all, { id: "jaipur-office", areaKey: null }, "302033", inJaipur), jaipurOffice);
  });

  it("keeps an outside pick until the location changes", () => {
    const pick = { id: "bengaluru", areaKey: "302033" };
    assert.equal(resolveAddress(all, pick, "302033", inJaipur), elsewhereDefault);
    const inMalviya = (a: CustomerAddress) => a.pincodeId === "302017";
    assert.equal(resolveAddress(all, pick, "302017", inMalviya), null);
  });

  it("drops a draft's outside or missing address for the area's own", () => {
    assert.equal(resolveAddress(all, { id: "bengaluru", areaKey: null }, "302033", inJaipur), jaipurHome);
    assert.equal(resolveAddress(all, { id: "deleted", areaKey: "302033" }, "302033", inJaipur), jaipurHome);
  });
});
