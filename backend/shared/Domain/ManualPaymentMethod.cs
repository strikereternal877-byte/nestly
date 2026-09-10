namespace Nestly.Domain;

/// <summary>
/// How an admin-recorded manual/offline payment was actually collected
/// (row 25 of docs/OPEN-FIXES-FEATURES.csv). Distinct from
/// <see cref="RefundMethod"/>, which describes how money is settled back
/// out, not how it came in. Kept intentionally minimal - the channels an
/// ops user is realistically asked to reconcile against a booking paid
/// outside the gateway - and extendable without a migration since it is
/// stored only as free text inside <see cref="PaymentAttempt.GatewayPaymentRef"/>
/// (see <c>PaymentWebhookService.RecordManualPaymentAsync</c>).
/// </summary>
public enum ManualPaymentMethod
{
    Cash,
    Upi,
    BankTransfer,
    Other
}
