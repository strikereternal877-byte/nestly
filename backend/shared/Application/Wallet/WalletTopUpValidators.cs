using FluentValidation;

namespace Nestly.Application.Wallet;

public class CreateWalletTopUpRequestValidator : AbstractValidator<CreateWalletTopUpRequest>
{
    public CreateWalletTopUpRequestValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0)
            .WithMessage("Enter an amount greater than zero.");

        // Paise at most: a gateway amount with a fraction of a paisa is rejected downstream, and a wallet
        // ledger carries two decimals (numeric(12,2)).
        RuleFor(x => x.Amount)
            .Must(amount => decimal.Round(amount, 2) == amount)
            .WithMessage("The amount can have at most two decimal places.");
    }
}
