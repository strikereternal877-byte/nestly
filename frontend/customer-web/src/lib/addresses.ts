import type { CustomerAddress } from "@/lib/types";

/**
 * Which saved address a booking page starts with, and keeps, as the customer
 * moves around (checkout and the monthly cook's review page).
 *
 * The booking is for the area chosen in the header - its slots, price and
 * serviceability are all that area's - so the address picked for the customer
 * is one inside it: their default address if it is there, else the first one
 * that is. An address outside the area is never picked for them; they can
 * still choose it themselves, and the page explains why it can't be booked.
 */
export function preferredAddress(
  addresses: readonly CustomerAddress[],
  inArea: (address: CustomerAddress) => boolean,
): CustomerAddress | null {
  const usable = addresses.filter(inArea);
  return usable.find((a) => a.isDefault) ?? usable[0] ?? null;
}

/** An address the customer chose, with the area (`areaKey`) it was chosen in - null when it came from a draft or a link rather than a click. */
export interface AddressPick {
  id: string;
  areaKey: string | null;
}

/**
 * The address shown as chosen: the customer's own pick while it still
 * applies - it is in the area, or the area hasn't changed since they picked
 * it (an outside address chosen on purpose stays until the location changes)
 * - else {@link preferredAddress}. A pick of an address that no longer exists
 * (a draft from another account, a deleted address) simply falls through.
 */
export function resolveAddress(
  addresses: readonly CustomerAddress[],
  pick: AddressPick | null,
  areaKey: string | null,
  inArea: (address: CustomerAddress) => boolean,
): CustomerAddress | null {
  const picked = pick ? addresses.find((a) => a.id === pick.id) : undefined;
  if (picked && (inArea(picked) || pick?.areaKey === areaKey)) return picked;
  return preferredAddress(addresses, inArea);
}
