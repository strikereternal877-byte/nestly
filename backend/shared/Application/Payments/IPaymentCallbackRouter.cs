using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.Payments;

/// <summary>
/// The single entry for a gateway callback. One gateway webhook URL serves every kind of payment this system
/// takes - a booking, a prepaid plan's checkout, a wallet top-up - and the callback only carries an order id,
/// so something has to find which of them it belongs to. This tries them in turn and returns the first
/// that recognises the order.
/// </summary>
public interface IPaymentCallbackRouter
{
    Task<Result> HandleAsync(PaymentWebhookRequest request);
}
