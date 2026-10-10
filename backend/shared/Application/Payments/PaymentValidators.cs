using FluentValidation;

namespace Nestly.Application.Payments;

public class CreatePaymentOrderRequestValidator : AbstractValidator<CreatePaymentOrderRequest>
{
    public CreatePaymentOrderRequestValidator()
    {
        RuleFor(x => x.BookingId).NotEmpty();
        RuleFor(x => x.IdempotencyKey).MaximumLength(100);
    }
}

public class PaymentWebhookRequestValidator : AbstractValidator<PaymentWebhookRequest>
{
    public PaymentWebhookRequestValidator()
    {
        RuleFor(x => x.GatewayOrderId).NotEmpty().MaximumLength(100);
        RuleFor(x => x.GatewayPaymentRef).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Status).NotEmpty();
        RuleFor(x => x.Signature).NotEmpty();
    }
}

public class SimulatePaymentRequestValidator : AbstractValidator<SimulatePaymentRequest>
{
    public SimulatePaymentRequestValidator()
    {
        RuleFor(x => x.GatewayOrderId).NotEmpty().MaximumLength(100);
    }
}

public class PayUWebhookFormPayloadValidator : AbstractValidator<PayUWebhookFormPayload>
{
    public PayUWebhookFormPayloadValidator()
    {
        RuleFor(x => x.Txnid).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Status).NotEmpty();
        RuleFor(x => x.Hash).NotEmpty();
    }
}
