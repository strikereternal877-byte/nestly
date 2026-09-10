using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Application.Payments;

/// <summary>
/// Handles the gateway's payment callback (SRS 30.1, 11.11.3, tasks 69a-c):
/// signature verification, idempotent duplicate handling, and applying the
/// outcome to the booking-payment mapping. Not scoped to a customer id - a
/// webhook is called by the gateway itself, authenticated by its signature
/// rather than a bearer token (SRS 28.3 "payment callback abuse").
/// </summary>
public interface IPaymentWebhookService
{
    Task<Result> HandleCallbackAsync(PaymentWebhookRequest request);

    /// <summary>
    /// Admin-recorded manual/offline payment (row 25, docs/OPEN-FIXES-FEATURES.csv) -
    /// transitions the booking exactly like a successful gateway payment
    /// (see the implementation's doc comment).
    /// </summary>
    Task<Result<PaymentTransaction>> RecordManualPaymentAsync(Guid bookingId, ManualPaymentMethod method, string reference);
}
