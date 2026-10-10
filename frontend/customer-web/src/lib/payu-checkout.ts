/**
 * Builds a hidden form and submits it - the only way to POST a full-page,
 * top-level navigation to PayU's Hosted Checkout (`fields` includes PayU's
 * signed hash; see PayUPaymentGateway.CreateOrderAsync on the backend for
 * where they come from). This never resolves - the browser navigates away
 * to PayU before any code after the call would run.
 *
 * Shared by every screen that pays through PayU: a booking's payment page and
 * the wallet "Add money" screen.
 */
export function submitToPayU(actionUrl: string, fields: Record<string, string>): void {
  const form = document.createElement("form");
  form.method = "POST";
  form.action = actionUrl;
  form.style.display = "none";
  for (const [name, value] of Object.entries(fields)) {
    const input = document.createElement("input");
    input.type = "hidden";
    input.name = name;
    input.value = value;
    form.appendChild(input);
  }
  document.body.appendChild(form);
  form.submit();
}
