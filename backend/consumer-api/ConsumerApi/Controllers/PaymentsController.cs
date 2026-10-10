using System.Globalization;
using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Nestly.Application.Payments;
using Nestly.BuildingBlocks.Extensions;

namespace Nestly.ConsumerApi.Controllers;

/// <summary>Payments (SRS 11.11, 30.1). Order creation/retry is scoped to the caller's own customer id; the webhook is not (see its own doc comment).</summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/payments")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _paymentService;
    private readonly IPaymentWebhookService _webhookService;
    private readonly IPaymentCallbackRouter _callbackRouter;
    private readonly IValidator<CreatePaymentOrderRequest> _createOrderValidator;
    private readonly IValidator<PaymentWebhookRequest> _webhookValidator;
    private readonly IValidator<SimulatePaymentRequest> _simulateValidator;
    private readonly IValidator<PayUWebhookFormPayload> _payUWebhookValidator;

    public PaymentsController(
        IPaymentService paymentService,
        IPaymentWebhookService webhookService,
        IPaymentCallbackRouter callbackRouter,
        IValidator<CreatePaymentOrderRequest> createOrderValidator,
        IValidator<PaymentWebhookRequest> webhookValidator,
        IValidator<SimulatePaymentRequest> simulateValidator,
        IValidator<PayUWebhookFormPayload> payUWebhookValidator)
    {
        _paymentService = paymentService;
        _webhookService = webhookService;
        _callbackRouter = callbackRouter;
        _createOrderValidator = createOrderValidator;
        _webhookValidator = webhookValidator;
        _simulateValidator = simulateValidator;
        _payUWebhookValidator = payUWebhookValidator;
    }

    /// <summary>
    /// Creates a gateway order for a booking's payment, or (task 70) retries
    /// after a prior failure - the same endpoint serves both, since a retry
    /// is just "create an order for a booking whose last attempt failed".
    /// Idempotent for a booking already awaiting a callback (task 68d).
    /// </summary>
    [HttpPost("orders")]
    [Authorize]
    [EnableRateLimiting("payment")]
    [ProducesResponseType(typeof(PaymentOrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateOrder([FromBody] CreatePaymentOrderRequest request)
    {
        var validation = await _createOrderValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _paymentService.CreateOrderAsync(CurrentCustomerId(), request);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : result.ToProblemResult();
    }

    /// <summary>Payment transaction + attempt history for a booking (SRS 11.11.3, 14.3, task 71).</summary>
    [HttpGet("bookings/{bookingId:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(PaymentTransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByBooking(Guid bookingId)
    {
        var result = await _paymentService.GetByBookingIdAsync(CurrentCustomerId(), bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// Actively re-checks a still-pending attempt against the gateway
    /// directly, rather than only waiting on its webhook - for a hosted-
    /// checkout gateway, a checkout the customer abandoned or cancelled
    /// before submitting payment details may never trigger a webhook at all,
    /// which otherwise leaves the booking stuck "confirming" until the
    /// unrelated 20-minute PaymentPending expiry sweep. Called by
    /// customer-web's payment return page once its own short client-side
    /// wait for a webhook elapses. A safe no-op (200 with the transaction
    /// unchanged) if the attempt is already resolved or the gateway itself
    /// still reports it as pending.
    /// </summary>
    [HttpPost("bookings/{bookingId:guid}/verify")]
    [Authorize]
    [EnableRateLimiting("payment")]
    [ProducesResponseType(typeof(PaymentTransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VerifyPending(Guid bookingId)
    {
        var result = await _paymentService.VerifyPendingAsync(CurrentCustomerId(), bookingId);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>
    /// The gateway's payment callback (SRS 30.1, 11.11.3, tasks 69a-c).
    /// Deliberately not [Authorize] - the caller is the payment gateway, not
    /// a logged-in customer, and is authenticated by its signature instead
    /// (SRS 28.3 "payment callback abuse"). Always idempotent (task 69b): a
    /// redelivered callback for an already-resolved attempt is a no-op 200,
    /// never re-applied.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    [EnableRateLimiting("payment-webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Webhook([FromBody] PaymentWebhookRequest request)
    {
        var validation = await _webhookValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        // Through the router, not the booking handler directly: the same webhook URL also receives wallet
        // top-up callbacks, which the router offers to the top-up handler once no booking claims the order.
        var result = await _callbackRouter.HandleAsync(request);
        return result.IsSuccess ? Ok() : result.ToProblemResult();
    }

    /// <summary>
    /// PayU's own callback shape (SRS 30.1, 11.11.3, tasks 69a-c) - separate
    /// from <see cref="Webhook"/> because PayU posts
    /// <c>application/x-www-form-urlencoded</c> fields with PayU-specific
    /// names, not this project's generic JSON <see cref="PaymentWebhookRequest"/>.
    /// Normalizes into that same request and runs it through the identical
    /// verify/idempotent-apply path <see cref="Webhook"/> does - PayU's
    /// signature is checked by <c>IPaymentGateway.BuildCanonicalPayload</c>/
    /// <c>VerifyWebhookSignature</c>, exactly like the sandbox's. Not
    /// [Authorize], same reasoning as <see cref="Webhook"/>.
    /// </summary>
    [HttpPost("webhook/payu")]
    [AllowAnonymous]
    [EnableRateLimiting("payment-webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PayUWebhook([FromForm] PayUWebhookFormPayload payload)
    {
        var validation = await _payUWebhookValidator.ValidateAsync(payload);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        // PayU can report "pending" for a mode with delayed settlement
        // (mainly netbanking). This project's webhook model is binary
        // (success/anything-else-is-failed, see PaymentWebhookService) - a
        // pending callback is acknowledged without being applied, so it
        // never prematurely flips the booking to PaymentFailed while PayU is
        // still going to deliver a final success/failure callback later.
        if (string.Equals(payload.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return Ok();
        }

        var request = new PaymentWebhookRequest(
            GatewayOrderId: payload.Txnid!,
            GatewayPaymentRef: payload.Mihpayid ?? string.Empty,
            Status: payload.Status!,
            Signature: payload.Hash!,
            Amount: decimal.TryParse(payload.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : null,
            ProductInfo: payload.Productinfo,
            FirstName: payload.Firstname,
            Email: payload.Email);

        var result = await _callbackRouter.HandleAsync(request);
        return result.IsSuccess ? Ok() : result.ToProblemResult();
    }

    /// <summary>
    /// Sandbox-only convenience (task 68b): simulates a gateway completing
    /// payment for an order the caller owns, deterministically per
    /// <c>SandboxPaymentGateway</c>'s amount convention, by constructing and
    /// signing the same callback <see cref="Webhook"/> handles for real.
    /// There is no equivalent endpoint for a real gateway integration - only
    /// the gateway itself can decide a payment's outcome.
    /// </summary>
    [HttpPost("orders/simulate")]
    [Authorize]
    [EnableRateLimiting("payment")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Simulate([FromBody] SimulatePaymentRequest request)
    {
        var validation = await _simulateValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _paymentService.SimulateAsync(CurrentCustomerId(), request);
        // NoContent, not Ok(): Ok() with no value still serialises a 200
        // with an empty body, which frontend/customer-web's apiFetch (only
        // special-cased for 204) then fails to JSON-parse - task 140b's E2E
        // suite caught this as a real "Failed to execute 'json' on
        // 'Response'" crash on the sandbox payment button, not a test-only
        // issue. NoContent matches every other no-body success response in
        // this codebase (see CategoriesController.Activate for the pattern).
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    private Guid CurrentCustomerId() =>
        User.GetSubjectId();

    private static ModelStateDictionary ToModelState(ValidationResult validation)
    {
        var modelState = new ModelStateDictionary();
        foreach (var error in validation.Errors)
        {
            modelState.AddModelError(error.PropertyName, error.ErrorMessage);
        }

        return modelState;
    }
}
