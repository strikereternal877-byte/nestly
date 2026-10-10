using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nestly.Application;
using Nestly.Application.Payments;
using Nestly.Application.Settings;
using Nestly.Application.Wallet;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Infrastructure.Observability;
using Nestly.Infrastructure.Options;
using Nestly.Infrastructure.Persistence;

namespace Nestly.Infrastructure.Services;

/// <summary>
/// A customer adding their own money to the wallet through the payment gateway. See
/// <see cref="WalletTopUp"/> for the model and <see cref="WalletTopUpOptions"/> for why the feature ships
/// switched off.
///
/// <para>
/// <b>The credit lands exactly once.</b> A gateway can deliver the same outcome by several routes (its
/// webhook, redelivered; the customer's return page asking us to verify; the reconciliation sweep), and any
/// two can race. <see cref="ResolveAsync"/> is the single place the wallet is credited: it flips the top-up
/// out of Pending with a conditional UPDATE and appends the ledger credit in the same serializable
/// transaction, so only one route can ever win and a loser changes nothing.
/// </para>
///
/// <para>
/// <b>A write-off is not final for success.</b> The sweep marks a top-up Failed once the gateway has no
/// completed payment for it, but a customer can still finish a slow checkout afterwards. A later success
/// callback therefore still credits (Failed -&gt; Success), because the alternative is money taken and never
/// credited.
/// </para>
/// </summary>
public class WalletTopUpService : IWalletTopUpService
{
    private const string Currency = "INR";

    private readonly IWalletTopUpRepository _repository;
    private readonly IWalletService _walletService;
    private readonly ICustomerRepository _customerRepository;
    private readonly IPaymentGateway _gateway;
    private readonly ISandboxPaymentSimulator _simulator;
    private readonly NestlyDbContext _context;
    private readonly ISystemSettingsService _settingsService;
    private readonly WalletTopUpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WalletTopUpService> _logger;

    public WalletTopUpService(
        IWalletTopUpRepository repository,
        IWalletService walletService,
        ICustomerRepository customerRepository,
        IPaymentGateway gateway,
        ISandboxPaymentSimulator simulator,
        NestlyDbContext context,
        ISystemSettingsService settingsService,
        IOptions<WalletTopUpOptions> options,
        TimeProvider timeProvider,
        ILogger<WalletTopUpService> logger)
    {
        _repository = repository;
        _walletService = walletService;
        _customerRepository = customerRepository;
        _gateway = gateway;
        _simulator = simulator;
        _context = context;
        _settingsService = settingsService;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<WalletTopUpConfigResponse> GetConfigAsync()
    {
        var (allowed, maxWalletBalance) = await ResolveSwitchAndCapAsync();
        return new WalletTopUpConfigResponse(
            allowed,
            _options.MinAmount,
            _options.MaxAmount,
            maxWalletBalance,
            ParseSuggestedAmounts());
    }

    /// <summary>
    /// Whether a new top-up may be started now, and the most a wallet may hold. Two switches must both be on:
    /// the deployment's <c>WalletTopUp:Enabled</c> (the deliberate, legal-groundwork opt-in) and the admin's
    /// "Allow wallet top-up" setting (the business's runtime kill switch). The balance cap is the lower of the
    /// deployment's and the admin's. An unreadable or never-seeded settings group fails closed: holding
    /// customers' money is not something to switch on by accident.
    ///
    /// <para>Only <i>starting</i> a top-up consults this - a top-up already in flight must still be able to
    /// finish, because the customer's money has already moved.</para>
    /// </summary>
    private async Task<(bool Allowed, decimal MaxWalletBalance)> ResolveSwitchAndCapAsync()
    {
        if (!_options.Enabled)
        {
            return (false, _options.MaxWalletBalance);
        }

        var settings = await _settingsService.GetWalletSettingsAsync();
        if (settings.IsFailure)
        {
            _logger.LogWarning(
                "Wallet settings are unavailable ({ErrorCode}); wallet top-ups are being refused until they are.",
                settings.Error.Code);
            return (false, _options.MaxWalletBalance);
        }

        return (settings.Value.AllowWalletTopUp, Math.Min(_options.MaxWalletBalance, settings.Value.MaxWalletBalance));
    }

    public async Task<Result<WalletTopUpOrderResponse>> CreateAsync(Guid customerId, decimal amount)
    {
        var (allowed, maxWalletBalance) = await ResolveSwitchAndCapAsync();
        if (!allowed)
        {
            return Error.Business("WalletTopUp.Disabled", "Adding money to your wallet isn't available right now.");
        }

        if (amount < _options.MinAmount || amount > _options.MaxAmount)
        {
            return Error.Validation(
                "WalletTopUp.AmountOutOfRange",
                $"You can add between {Rupees(_options.MinAmount)} and {Rupees(_options.MaxAmount)} at a time.");
        }

        var customer = await _customerRepository.GetByIdAsync(customerId);
        if (customer is null)
        {
            return Error.NotFound("WalletTopUp.CustomerNotFound", "Your account could not be found.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // A double-click, a reload or a retry must not open a second checkout for the same money: hand back the
        // one already in flight, rebuilt against its own order id (the webhook looks the top-up up by it).
        var inFlight = await _repository.FindRecentPendingAsync(customerId, amount, now.AddMinutes(-_options.PendingReuseMinutes));
        if (inFlight is not null)
        {
            return Result.Success(await ToOrderResponseAsync(inFlight, customer));
        }

        if (await _repository.CountCreatedSinceAsync(customerId, now.AddHours(-24)) >= _options.MaxTopUpsPerDay)
        {
            return Error.Business(
                "WalletTopUp.DailyLimit",
                "You've reached the number of top-ups allowed in a day. Please try again tomorrow.");
        }

        var balance = (await _walletService.GetBalanceAsync(customerId)).Value.Balance;
        var awaiting = await _repository.SumRecentPendingAsync(customerId, now.AddHours(-24));
        if (balance + awaiting + amount > maxWalletBalance)
        {
            var room = Math.Max(0m, maxWalletBalance - balance - awaiting);
            return Error.Business(
                "WalletTopUp.BalanceLimit",
                room > 0
                    ? $"Your wallet can hold at most {Rupees(maxWalletBalance)}. You can add up to {Rupees(room)} right now."
                    : $"Your wallet is at its {Rupees(maxWalletBalance)} limit. Spend some of it before adding more.");
        }

        var topUpId = Guid.NewGuid();
        var order = await _gateway.CreateOrderAsync(BuildGatewayRequest(topUpId, amount, customer, existingGatewayOrderId: null));

        var topUp = new WalletTopUp(topUpId, customerId, amount, Currency, order.GatewayOrderId);
        await _repository.AddAsync(topUp);

        _logger.LogInformation("Wallet top-up {TopUpId} of {Amount} started for customer {CustomerId}.", topUp.Id, amount, customerId);
        return Result.Success(ToOrderResponse(topUp, order));
    }

    public async Task<Result<WalletTopUpResponse>> GetAsync(Guid customerId, Guid topUpId)
    {
        var topUp = await _repository.GetByIdAsync(topUpId);
        if (topUp is null || topUp.CustomerId != customerId)
        {
            return Error.NotFound("WalletTopUp.NotFound", "That top-up does not exist.");
        }

        return Result.Success(await ToResponseAsync(topUp));
    }

    public async Task<Result<WalletTopUpResponse>> VerifyPendingAsync(Guid customerId, Guid topUpId)
    {
        // Ownership first: verifying calls the gateway and can resolve state, which must never be triggerable
        // for someone else's top-up.
        var topUp = await _repository.GetByIdAsync(topUpId);
        if (topUp is null || topUp.CustomerId != customerId)
        {
            return Error.NotFound("WalletTopUp.NotFound", "That top-up does not exist.");
        }

        if (topUp.Status == WalletTopUpStatus.Pending)
        {
            await VerifyWithGatewayAsync(topUp);
            topUp = await _repository.GetByIdAsync(topUpId) ?? topUp;
        }

        return Result.Success(await ToResponseAsync(topUp));
    }

    public async Task<Result<WalletTopUpResponse>> SimulateAsync(Guid customerId, Guid topUpId)
    {
        // The one real enforcement of "sandbox only", the same type check PaymentService.SimulateAsync makes:
        // ISandboxPaymentSimulator is bound to the sandbox whichever gateway is active, so without this an
        // authenticated customer could credit their own wallet against a real gateway order with no money paid.
        if (_gateway is not SandboxPaymentGateway)
        {
            return Error.Business(
                "WalletTopUp.SimulateNotAvailable",
                "Payment simulation is not available: a real payment gateway is configured.");
        }

        var topUp = await _repository.GetByIdAsync(topUpId);
        if (topUp is null || topUp.CustomerId != customerId)
        {
            return Error.NotFound("WalletTopUp.NotFound", "That top-up does not exist.");
        }

        var outcome = _simulator.DetermineOutcome(topUp.Amount);
        string status = outcome.Succeeded ? PaymentWebhookPayload.SuccessStatus : PaymentWebhookPayload.FailedStatus;
        string paymentRef = outcome.Succeeded ? outcome.GatewayPaymentRef : $"sandbox_declined_{Guid.NewGuid():N}";
        string signature = _simulator.SignPayload(PaymentWebhookPayload.Build(topUp.GatewayOrderId, paymentRef, status));

        // Through the real callback handler, so signature, amount and idempotency checks are not skipped.
        var callback = await HandleCallbackAsync(new PaymentWebhookRequest(topUp.GatewayOrderId, paymentRef, status, signature));
        if (callback.IsFailure)
        {
            return callback.Error;
        }

        return Result.Success(await ToResponseAsync(await _repository.GetByIdAsync(topUpId) ?? topUp));
    }

    public async Task<Result> HandleCallbackAsync(PaymentWebhookRequest request)
    {
        string canonicalPayload = _gateway.BuildCanonicalPayload(request);
        if (!_gateway.VerifyWebhookSignature(canonicalPayload, request.Signature))
        {
            // The order id is unverified caller input at this point, so it is sanitised before it reaches the log.
            _logger.LogWarning(
                "Rejected a wallet top-up webhook with an invalid signature for gateway order {GatewayOrderId}.",
                LogSanitizer.ForLog(request.GatewayOrderId));
            return Result.Failure(Error.Unauthorized("Payment.InvalidWebhookSignature", "The webhook signature could not be verified."));
        }

        var topUp = await _repository.GetByGatewayOrderIdAsync(request.GatewayOrderId);
        if (topUp is null)
        {
            // Not ours - the caller (IPaymentCallbackRouter) treats this code as "try the next kind of payment".
            return Result.Failure(Error.NotFound("Payment.OrderNotFound", "No payment attempt exists for this gateway order."));
        }

        bool succeeded = string.Equals(request.Status, PaymentWebhookPayload.SuccessStatus, StringComparison.OrdinalIgnoreCase);

        // Redelivery: the first resolution wins. The one exception is a late success after a write-off (see the
        // class comment); a late failure after anything is just noise.
        if (topUp.Status == WalletTopUpStatus.Success || (topUp.Status == WalletTopUpStatus.Failed && !succeeded))
        {
            _logger.LogInformation(
                "Ignored a duplicate wallet top-up webhook for gateway order {GatewayOrderId} (top-up already {Status}).",
                LogSanitizer.ForLog(topUp.GatewayOrderId), topUp.Status);
            return Result.Success();
        }

        // The signature already covers the amount (for PayU), but the wallet is credited from OUR record of what was
        // asked for, so a callback that disagrees with it is never credited - it is left for a person to look at.
        if (succeeded && request.Amount is { } paid && paid != topUp.Amount)
        {
            _logger.LogError(
                "Wallet top-up {TopUpId}: the gateway reports {Paid} paid but {Expected} was requested. Not credited; needs manual review.",
                topUp.Id, paid, topUp.Amount);

            // Surface it in the admin top-up list as well as the log, where a person will actually see it.
            if (!topUp.NeedsReview)
            {
                topUp.FlagForReview(
                    $"The gateway reported {Rupees(paid)} paid but {Rupees(topUp.Amount)} was requested. Nothing was credited.");
                await _repository.UpdateAsync(topUp);
            }

            return Result.Failure(Error.Business("WalletTopUp.AmountMismatch", "The amount paid does not match the top-up."));
        }

        await ResolveAsync(topUp, succeeded, request.GatewayPaymentRef, succeeded ? null : request.Status);
        return Result.Success();
    }

    public async Task<bool> ReconcileAsync(Guid topUpId, CancellationToken cancellationToken = default)
    {
        var topUp = await _repository.GetByIdAsync(topUpId);
        if (topUp is null || topUp.Status != WalletTopUpStatus.Pending)
        {
            return false;
        }

        var outcome = await VerifyWithGatewayAsync(topUp);
        return outcome is WalletTopUpReconcileOutcome.Credited or WalletTopUpReconcileOutcome.MarkedFailed;
    }

    public async Task<Result<WalletTopUpReconcileOutcome>> ReconcileNowAsync(Guid topUpId, CancellationToken cancellationToken = default)
    {
        var topUp = await _repository.GetByIdAsync(topUpId);
        if (topUp is null)
        {
            return Error.NotFound("WalletTopUp.NotFound", "That top-up does not exist.");
        }

        // A Failed top-up is asked about too: a payment that lands after the sweep wrote it off must still credit
        // (see WalletTopUp.MarkSucceeded), and an admin chasing a customer's "money was deducted" report needs to
        // be able to make that happen without waiting for a webhook.
        if (topUp.Status == WalletTopUpStatus.Success)
        {
            return WalletTopUpReconcileOutcome.Unchanged;
        }

        return await VerifyWithGatewayAsync(topUp);
    }

    /// <summary>Asks the gateway and applies a definite answer; "pending" (including a transport error) changes nothing.</summary>
    private async Task<WalletTopUpReconcileOutcome> VerifyWithGatewayAsync(WalletTopUp topUp)
    {
        var verify = await _gateway.VerifyOrderStatusAsync(topUp.GatewayOrderId);
        if (string.Equals(verify.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return WalletTopUpReconcileOutcome.StillPending;
        }

        bool succeeded = string.Equals(verify.Status, PaymentWebhookPayload.SuccessStatus, StringComparison.OrdinalIgnoreCase);
        bool applied = await ResolveAsync(
            topUp, succeeded, verify.GatewayPaymentRef,
            succeeded ? null : verify.FailureReason ?? "The payment was not completed.");

        if (!applied)
        {
            // Nothing to apply: another route already resolved it, or it was already Failed and the gateway still says so.
            return WalletTopUpReconcileOutcome.Unchanged;
        }

        return succeeded ? WalletTopUpReconcileOutcome.Credited : WalletTopUpReconcileOutcome.MarkedFailed;
    }

    /// <summary>
    /// The only place the wallet is credited. Returns false when another route had already applied an outcome.
    /// Serializable, like <c>WalletService.DebitAsync</c>: the credit reads the latest balance and appends the
    /// next ledger row, which two concurrent writers must not both do from the same starting point.
    /// </summary>
    private async Task<bool> ResolveAsync(WalletTopUp topUp, bool succeeded, string? gatewayPaymentRef, string? failureReason)
    {
        var ambient = _context.Database.CurrentTransaction;
        var own = ambient is null ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable) : null;

        try
        {
            bool won = await _repository.TryMarkResolvedAsync(topUp.Id, succeeded ? WalletTopUpStatus.Success : WalletTopUpStatus.Failed);
            if (!won)
            {
                if (own is not null)
                {
                    await own.CommitAsync();
                }

                return false;
            }

            if (succeeded)
            {
                var entry = await _walletService.CreditAsync(
                    topUp.CustomerId, topUp.Amount, WalletSourceType.TopUp, topUp.Id, "Wallet top-up");
                topUp.MarkSucceeded(gatewayPaymentRef ?? string.Empty, entry.Id);
            }
            else
            {
                topUp.MarkFailed(failureReason);
            }

            await _repository.UpdateAsync(topUp);

            if (own is not null)
            {
                await own.CommitAsync();
            }

            _logger.LogInformation(
                "Wallet top-up {TopUpId} resolved as {Outcome} for customer {CustomerId}.",
                topUp.Id, succeeded ? "success" : "failure", topUp.CustomerId);
            return true;
        }
        catch
        {
            if (own is not null)
            {
                await own.RollbackAsync();
            }

            throw;
        }
        finally
        {
            own?.Dispose();
        }
    }

    private GatewayCreateOrderRequest BuildGatewayRequest(Guid topUpId, decimal amount, Customer customer, string? existingGatewayOrderId) => new(
        topUpId, amount, Currency, topUpId.ToString("N"),
        CustomerName: customer.Name, CustomerMobile: customer.Mobile, CustomerEmail: customer.Email,
        ExistingGatewayOrderId: existingGatewayOrderId,
        ReturnPath: $"/wallet/topup/{topUpId}/return",
        ProductInfo: "Glavyx wallet top-up");

    private async Task<WalletTopUpOrderResponse> ToOrderResponseAsync(WalletTopUp topUp, Customer customer)
    {
        var order = await _gateway.CreateOrderAsync(BuildGatewayRequest(topUp.Id, topUp.Amount, customer, topUp.GatewayOrderId));
        return ToOrderResponse(topUp, order);
    }

    private static WalletTopUpOrderResponse ToOrderResponse(WalletTopUp topUp, GatewayOrderResult order) => new(
        topUp.Id, topUp.GatewayOrderId, topUp.Amount, topUp.Currency, order.CheckoutRedirectUrl, order.CheckoutFormFields);

    private async Task<WalletTopUpResponse> ToResponseAsync(WalletTopUp topUp)
    {
        var balance = (await _walletService.GetBalanceAsync(topUp.CustomerId)).Value.Balance;
        return new WalletTopUpResponse(
            topUp.Id, topUp.Amount, topUp.Currency, topUp.Status, topUp.FailureReason,
            topUp.CreatedAtUtc, topUp.CompletedAtUtc, balance);
    }

    private IReadOnlyList<decimal> ParseSuggestedAmounts() =>
        (_options.SuggestedAmounts ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => decimal.TryParse(part, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : -1m)
            .Where(value => value >= _options.MinAmount && value <= _options.MaxAmount)
            .Distinct()
            .OrderBy(value => value)
            .ToList();

    private static string Rupees(decimal amount) => $"₹{amount:0.##}";
}
