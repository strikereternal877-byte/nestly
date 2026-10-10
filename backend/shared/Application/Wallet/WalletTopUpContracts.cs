using Nestly.Domain;

namespace Nestly.Application.Wallet;

/// <summary>Body of "add money to my wallet". The amount is in rupees, at most two decimals.</summary>
public record CreateWalletTopUpRequest(decimal Amount);

/// <summary>
/// What the "Add money" screen needs before it can offer anything: whether top-ups are switched on at all,
/// and the limits it must respect. <see cref="Enabled"/> false means the screen should not offer the feature.
/// </summary>
public record WalletTopUpConfigResponse(
    bool Enabled,
    decimal MinAmount,
    decimal MaxAmount,
    decimal MaxWalletBalance,
    IReadOnlyList<decimal> SuggestedAmounts);

/// <summary>
/// The gateway checkout to send the customer to. <paramref name="CheckoutRedirectUrl"/> and
/// <paramref name="CheckoutFormFields"/> are populated only for a hosted-checkout gateway (PayU) and are null for
/// the sandbox, whose client-side flow is the <c>simulate</c> endpoint instead - the same shape as a booking's
/// payment order.
/// </summary>
public record WalletTopUpOrderResponse(
    Guid TopUpId,
    string GatewayOrderId,
    decimal Amount,
    string Currency,
    string? CheckoutRedirectUrl = null,
    IReadOnlyDictionary<string, string>? CheckoutFormFields = null);

/// <param name="WalletBalance">The balance now, so the return page can show the result without a second round trip.</param>
public record WalletTopUpResponse(
    Guid Id,
    decimal Amount,
    string Currency,
    WalletTopUpStatus Status,
    string? FailureReason,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    decimal WalletBalance);
