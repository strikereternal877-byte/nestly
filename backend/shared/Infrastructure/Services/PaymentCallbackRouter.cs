using Nestly.Application.Payments;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Results;

namespace Nestly.Infrastructure.Services;

/// <summary>See <see cref="IPaymentCallbackRouter"/>.</summary>
public class PaymentCallbackRouter : IPaymentCallbackRouter
{
    /// <summary>The code both handlers return for "this order id is not one of mine".</summary>
    private const string OrderNotFoundCode = "Payment.OrderNotFound";

    private readonly IPaymentWebhookService _bookingPayments;
    private readonly IWalletTopUpService _walletTopUps;

    public PaymentCallbackRouter(IPaymentWebhookService bookingPayments, IWalletTopUpService walletTopUps)
    {
        _bookingPayments = bookingPayments;
        _walletTopUps = walletTopUps;
    }

    public async Task<Result> HandleAsync(PaymentWebhookRequest request)
    {
        // Booking payments (single or a prepaid plan's grouped checkout) first - by far the common case and the
        // behaviour this endpoint has always had. Only an order id none of them recognises is offered to the
        // wallet top-ups; a real failure (a bad signature, a data problem) is returned as it is, never retried
        // against the next handler.
        var result = await _bookingPayments.HandleCallbackAsync(request);
        if (result.IsFailure && result.Error.Code == OrderNotFoundCode)
        {
            return await _walletTopUps.HandleCallbackAsync(request);
        }

        return result;
    }
}
