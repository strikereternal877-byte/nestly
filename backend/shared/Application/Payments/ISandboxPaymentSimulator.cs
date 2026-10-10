namespace Nestly.Application.Payments;

/// <summary>
/// Sandbox-only capability, deliberately separate from <see cref="IPaymentGateway"/>:
/// a real gateway has no "decide the outcome for me" API - the customer's
/// bank/card does that. This lets the sandbox "simulate payment" endpoint
/// (task 68b) ask, in one explicit, self-documenting call, what a sandbox
/// order should resolve to, without leaking that sandbox-only concern into
/// the vendor-agnostic <see cref="IPaymentGateway"/> abstraction.
/// </summary>
public interface ISandboxPaymentSimulator
{
    /// <summary>
    /// Deterministic outcome for completing payment on a sandbox order of
    /// <paramref name="amount"/> (see <c>SandboxPaymentGateway</c> for the
    /// exact convention). Never random - QA (task 79) and any caller must be
    /// able to force either outcome on demand by choosing the amount.
    /// </summary>
    SandboxPaymentOutcome DetermineOutcome(decimal amount);

    /// <summary>
    /// Signs a payload the same way the sandbox signs its own simulated
    /// callback. Lives here rather than on <see cref="IPaymentGateway"/>
    /// (which it used to be part of) because it is only ever meaningful for
    /// the sandbox: since there is no real gateway to call our webhook, the
    /// sandbox "simulate" endpoint uses this to construct a callback payload
    /// with a valid signature, so the actual webhook handler -
    /// <c>IPaymentGateway.VerifyWebhookSignature</c> and everything
    /// downstream of it - is exercised for real rather than bypassed. This
    /// interface is always bound to the concrete sandbox regardless of which
    /// <see cref="IPaymentGateway"/> is active (see
    /// <c>PaymentGatewayRegistration</c>), so callers that need this - only
    /// <c>PaymentService.SimulateAsync</c> - resolve it through here, never
    /// through <see cref="IPaymentGateway"/>.
    /// </summary>
    string SignPayload(string canonicalPayload);
}

public sealed record SandboxPaymentOutcome(bool Succeeded, string GatewayPaymentRef, string? FailureReason);
