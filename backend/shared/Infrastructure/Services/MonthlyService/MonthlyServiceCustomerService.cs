using Microsoft.Extensions.Logging;
using Nestly.Application;
using Nestly.Application.MonthlyService;
using Nestly.Application.Payments;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;
using Nestly.Domain.Meals;
using Nestly.Domain.MonthlyService;

namespace Nestly.Infrastructure.Services.MonthlyService;

/// <inheritdoc cref="IMonthlyServiceCustomerService"/>
public class MonthlyServiceCustomerService : IMonthlyServiceCustomerService
{
    private readonly IMonthlyServicePlanRepository _planRepository;
    private readonly IMonthlyServiceContractRepository _contractRepository;
    private readonly IMonthlyServiceAttendanceRepository _attendanceRepository;
    private readonly IMonthlyServiceInvoiceRepository _invoiceRepository;
    private readonly ICustomerAddressRepository _addressRepository;
    private readonly IServiceRepository _serviceRepository;
    private readonly IPaymentGateway _paymentGateway;
    private readonly ISandboxPaymentSimulator _paymentSimulator;
    private readonly MonthlyServiceEngine _engine;
    private readonly ILogger<MonthlyServiceCustomerService> _logger;

    public MonthlyServiceCustomerService(
        IMonthlyServicePlanRepository planRepository,
        IMonthlyServiceContractRepository contractRepository,
        IMonthlyServiceAttendanceRepository attendanceRepository,
        IMonthlyServiceInvoiceRepository invoiceRepository,
        ICustomerAddressRepository addressRepository,
        IServiceRepository serviceRepository,
        IPaymentGateway paymentGateway,
        ISandboxPaymentSimulator paymentSimulator,
        MonthlyServiceEngine engine,
        ILogger<MonthlyServiceCustomerService> logger)
    {
        _planRepository = planRepository;
        _contractRepository = contractRepository;
        _attendanceRepository = attendanceRepository;
        _invoiceRepository = invoiceRepository;
        _addressRepository = addressRepository;
        _serviceRepository = serviceRepository;
        _paymentGateway = paymentGateway;
        _paymentSimulator = paymentSimulator;
        _engine = engine;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MonthlyServicePlanBrowseResponse>> BrowsePlansAsync(Guid? cityId)
    {
        var plans = await _planRepository.ListAsync(activeOnly: true, cityId);
        var services = await _engine.ServiceNamesAsync(plans.Select(p => p.ServiceId));
        var slugs = await _engine.ServiceSlugsAsync(plans.Select(p => p.ServiceId));
        var cities = await _engine.CityNamesAsync(plans.Select(p => p.CityId));
        return plans.Select(p => new MonthlyServicePlanBrowseResponse(
            p.Id, p.ServiceId, services.GetValueOrDefault(p.ServiceId, string.Empty),
            p.CityId, cities.GetValueOrDefault(p.CityId, string.Empty),
            p.Name, p.Description, p.Basis, p.HoursPerVisit, p.IncludedTasks, p.RatePerVisit, p.Frequency, p.TimesPerPeriod,
            slugs.GetValueOrDefault(p.ServiceId), p.MonthlyDiscountPercent)).ToList();
    }

    public async Task<Result<MonthlyServiceContractResponse>> RequestContractAsync(Guid customerId, MonthlyServiceContractRequest request)
    {
        var context = await LoadRequestContextAsync(customerId, request.PlanId, request.AddressId, request.StartDate);
        if (context.IsFailure)
        {
            return context.Error;
        }

        var (plan, address, existing) = context.Value;

        if (!MonthlyServiceTimeFormat.TryParse(request.VisitStartTime, out var visitStart))
        {
            return Error.Validation("MonthlyService.InvalidTime", "Visit time must be in HH:mm format.");
        }

        if (IsDuplicate(existing, address.Id, plan.ServiceId, mealSession: null))
        {
            return Error.Conflict("MonthlyService.DuplicateContract", "You already have this service running (or requested) at this address.");
        }

        MonthlyServiceContract contract;
        try
        {
            contract = new MonthlyServiceContract(
                Guid.NewGuid(), customerId, plan, address.Id,
                MonthlyServiceEngine.ToWeekdays(request.Days), visitStart,
                request.StartDate, request.EndDate, request.Note, _engine.NowUtc,
                MonthDays.ToMask(request.MonthDates ?? []),
                payFromWallet: request.PayFromWallet,
                billingMode: request.BillingMode);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Error.Validation("MonthlyService.InvalidRequest", MonthlyServiceEngine.UserMessage(ex));
        }

        await _contractRepository.AddAsync(contract);
        _logger.LogInformation("Monthly service contract {ContractId} requested on plan {PlanId}.", contract.Id, plan.Id);
        return await ToResponseAsync(contract);
    }

    public async Task<Result<IReadOnlyList<MonthlyServiceContractResponse>>> RequestMealContractsAsync(Guid customerId, MonthlyServiceMealContractRequest request)
    {
        var context = await LoadRequestContextAsync(customerId, request.PlanId, request.AddressId, request.StartDate);
        if (context.IsFailure)
        {
            return context.Error;
        }

        var (plan, address, existing) = context.Value;
        if (plan.Basis != MonthlyServicePlanBasis.MealBased)
        {
            return Error.Validation("MonthlyService.NotMealPlan", "This plan is not priced by meals.");
        }

        var service = await _serviceRepository.GetByIdAsync(plan.ServiceId);
        if (service is null || service.PricingType != ServicePricingType.MealBased || service.MealOptions.Count == 0)
        {
            return Error.Business("MonthlyService.MealRatesMissing", "Meal rates are not set up for this service yet.");
        }

        IReadOnlyList<MealVisitQuote> quotes;
        try
        {
            quotes = MealPricing.Quote(service.MealOptions, request.Meals, request.Persons);
        }
        catch (ArgumentException ex)
        {
            return Error.Validation("MonthlyService.InvalidMeals", ex.Message);
        }

        // One contract per session (docs/MEAL-SERVICE.md): each has its own
        // rate, visit time and length, so attendance, billing and the
        // professional's clash check need no meal awareness at all.
        var contracts = new List<MonthlyServiceContract>(quotes.Count);
        foreach (var quote in quotes)
        {
            var requestedTime = quote.Session == MealSession.Morning ? request.MorningVisitTime : request.EveningVisitTime;
            var visitStart = quote.DefaultStartTime;
            if (requestedTime is not null && !MonthlyServiceTimeFormat.TryParse(requestedTime, out visitStart))
            {
                return Error.Validation("MonthlyService.InvalidTime", "Visit time must be in HH:mm format.");
            }

            if (IsDuplicate(existing, address.Id, plan.ServiceId, quote.Session))
            {
                return Error.Conflict("MonthlyService.DuplicateContract",
                    $"You already have a {quote.Session.ToString().ToLowerInvariant()} cook running (or requested) at this address.");
            }

            try
            {
                contracts.Add(new MonthlyServiceContract(
                    Guid.NewGuid(), customerId, plan, address.Id,
                    MonthlyServiceEngine.ToWeekdays(request.Days), visitStart,
                    request.StartDate, request.EndDate, request.Note, _engine.NowUtc,
                    mealQuote: quote,
                    payFromWallet: request.PayFromWallet,
                    billingMode: request.BillingMode));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Error.Validation("MonthlyService.InvalidRequest", MonthlyServiceEngine.UserMessage(ex));
            }
        }

        // Every visit is validated before any is saved, so a bad evening
        // visit never leaves a lone morning one behind.
        var responses = new List<MonthlyServiceContractResponse>(contracts.Count);
        foreach (var contract in contracts)
        {
            await _contractRepository.AddAsync(contract);
            _logger.LogInformation("Monthly meal contract {ContractId} ({Session}) requested on plan {PlanId}.", contract.Id, contract.MealSession, plan.Id);
            responses.Add(await ToResponseAsync(contract));
        }

        return Result.Success<IReadOnlyList<MonthlyServiceContractResponse>>(responses);
    }

    /// <summary>The checks every request shares: plan open, address the caller's and in the plan's city, start not in the past.</summary>
    private async Task<Result<(MonthlyServicePlan Plan, CustomerAddress Address, IReadOnlyList<MonthlyServiceContract> Existing)>> LoadRequestContextAsync(
        Guid customerId, Guid planId, Guid addressId, DateOnly startDate)
    {
        var plan = await _planRepository.GetByIdAsync(planId);
        if (plan is null || !plan.IsActive)
        {
            return Error.NotFound("MonthlyService.PlanNotFound", "This plan is not available.");
        }

        var address = await _addressRepository.GetByIdAsync(addressId);
        if (address is null || address.CustomerId != customerId)
        {
            return Error.NotFound("MonthlyService.AddressNotFound", "The selected address was not found.");
        }

        if (!await _engine.AddressIsInCityAsync(address, plan.CityId))
        {
            return Error.Business("MonthlyService.AddressOutsideCity", "This plan is not offered in the selected address's city.");
        }

        if (startDate < _engine.Today)
        {
            return Error.Validation("MonthlyService.StartDateInPast", "Start date cannot be in the past.");
        }

        var existing = await _contractRepository.ListByCustomerAsync(customerId);
        return (plan, address, existing);
    }

    /// <summary>One live contract per service per address - per meal session for a cook, so morning and evening can both run.</summary>
    private static bool IsDuplicate(IEnumerable<MonthlyServiceContract> existing, Guid addressId, Guid serviceId, MealSession? mealSession) =>
        existing.Any(c => c.Status != MonthlyServiceContractStatus.Cancelled
            && c.AddressId == addressId
            && c.ServiceIdSnapshot == serviceId
            && c.MealSession == mealSession);

    public async Task<IReadOnlyList<MonthlyServiceContractResponse>> ListContractsAsync(Guid customerId)
    {
        var contracts = await _contractRepository.ListByCustomerAsync(customerId);
        var result = new List<MonthlyServiceContractResponse>(contracts.Count);
        foreach (var contract in contracts)
        {
            result.Add(await ToResponseAsync(contract));
        }

        return result;
    }

    public async Task<Result<MonthlyServiceContractResponse>> GetContractAsync(Guid customerId, Guid contractId)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId);
        if (contract is null || contract.CustomerId != customerId)
        {
            return ContractNotFound();
        }

        return await ToResponseAsync(contract);
    }

    public async Task<Result<MonthlyServiceAttendanceMonthResponse>> GetAttendanceAsync(Guid customerId, Guid contractId, int year, int month)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId);
        if (contract is null || contract.CustomerId != customerId)
        {
            return ContractNotFound();
        }

        return await _engine.MonthAsync(contract, year, month, MonthlyServiceViewer.Customer);
    }

    public async Task<Result> CancelContractAsync(Guid customerId, Guid contractId, string? reason)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId);
        if (contract is null || contract.CustomerId != customerId)
        {
            return Result.Failure(ContractNotFound());
        }

        try
        {
            contract.Cancel(reason, _engine.NowUtc);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Business("MonthlyService.CannotCancel", MonthlyServiceEngine.UserMessage(ex)));
        }

        await _contractRepository.UpdateAsync(contract);
        await _engine.RemoveAllUpcomingAsync(contract);
        _logger.LogInformation("Monthly service contract {ContractId} cancelled by the customer.", contract.Id);
        return Result.Success();
    }

    public Task<Result<MonthlyServiceAttendanceItem>> SkipAsync(Guid customerId, Guid attendanceId) =>
        MutateAsync(customerId, attendanceId, row => row.Skip(_engine.NowLocal, _engine.NowUtc, _engine.Policy));

    public Task<Result<MonthlyServiceAttendanceItem>> UnskipAsync(Guid customerId, Guid attendanceId) =>
        MutateAsync(customerId, attendanceId, row => row.Unskip(_engine.NowLocal, _engine.NowUtc, _engine.Policy));

    /// <summary>Daily wallet: books a day that was off for low balance, paying for it now (refused if the wallet still can't).</summary>
    public Task<Result<MonthlyServiceAttendanceItem>> RebookAsync(Guid customerId, Guid attendanceId) =>
        MutateAsync(customerId, attendanceId, row => row.Rebook(_engine.NowLocal, _engine.Policy));

    public Task<Result<MonthlyServiceAttendanceItem>> ConfirmVisitAsync(Guid customerId, Guid attendanceId) =>
        MutateAsync(customerId, attendanceId, row => row.ConfirmByCustomer(_engine.NowLocal, _engine.NowUtc, _engine.Policy));

    public Task<Result<MonthlyServiceAttendanceItem>> DisputeAsync(Guid customerId, Guid attendanceId, string reason) =>
        MutateAsync(customerId, attendanceId, row => row.RaiseDispute(reason, _engine.NowUtc, _engine.Policy));

    public async Task<IReadOnlyList<MonthlyServiceInvoiceResponse>> ListInvoicesAsync(Guid customerId) =>
        await _engine.ToInvoiceResponsesAsync(await _invoiceRepository.ListByCustomerAsync(customerId), includeCommission: false);

    public async Task<Result<MonthlyServiceInvoiceResponse>> PayInvoiceAsync(Guid customerId, Guid invoiceId)
    {
        var invoice = await _invoiceRepository.GetByIdAsync(invoiceId);
        if (invoice is null || invoice.CustomerId != customerId)
        {
            return Error.NotFound("MonthlyService.InvoiceNotFound", "Invoice not found.");
        }

        if (invoice.IsPaid)
        {
            return Error.Business("MonthlyService.AlreadyPaid", "This invoice is already paid.");
        }

        // Same gateway seam (and sandbox outcome) SubscriptionBillingJob uses
        // for its own non-booking charge - docs/MONTHLY-SERVICE.md OPEN DECISIONS #1.
        await _paymentGateway.CreateOrderAsync(
            new GatewayCreateOrderRequest(invoice.Id, invoice.Amount, "INR", invoice.Id.ToString("N")));
        var outcome = _paymentSimulator.DetermineOutcome(invoice.Amount);
        if (!outcome.Succeeded)
        {
            return Error.Business("MonthlyService.PaymentFailed", outcome.FailureReason ?? "Payment was declined. Please try again.");
        }

        await _engine.CompletePaymentAsync(invoice, MonthlyServicePaymentMethod.Online, outcome.GatewayPaymentRef, adminUserId: null);
        _logger.LogInformation("Monthly service invoice {InvoiceId} paid online.", invoice.Id);
        var responses = await _engine.ToInvoiceResponsesAsync([invoice], includeCommission: false);
        return responses[0];
    }

    public async Task<Result<MonthlyServiceInvoiceResponse>> PayInvoiceFromWalletAsync(Guid customerId, Guid invoiceId)
    {
        var invoice = await _invoiceRepository.GetByIdAsync(invoiceId);
        if (invoice is null || invoice.CustomerId != customerId)
        {
            return Error.NotFound("MonthlyService.InvoiceNotFound", "Invoice not found.");
        }

        var paid = await _engine.PayFromWalletAsync(invoice);
        if (paid.IsFailure)
        {
            return paid.Error.Code == "Wallet.InsufficientBalance"
                ? Error.Business("MonthlyService.WalletBalanceLow", "Your wallet balance is less than this bill. Add money to your wallet or pay online.")
                : paid.Error;
        }

        var responses = await _engine.ToInvoiceResponsesAsync([invoice], includeCommission: false);
        return responses[0];
    }

    public async Task<Result<MonthlyServiceContractResponse>> SetWalletAutopayAsync(Guid customerId, Guid contractId, bool enabled)
    {
        var contract = await _contractRepository.GetByIdAsync(contractId);
        if (contract is null || contract.CustomerId != customerId)
        {
            return ContractNotFound();
        }

        try
        {
            contract.SetPayFromWallet(enabled, _engine.NowUtc);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Business("MonthlyService.InvalidState", ex.Message);
        }

        await _contractRepository.UpdateAsync(contract);

        // Turning it on settles what is already owed, if the balance allows,
        // rather than leaving it for tomorrow's run.
        if (enabled)
        {
            foreach (var invoice in (await _invoiceRepository.ListByContractAsync(contract.Id)).Where(i => !i.IsPaid))
            {
                await _engine.PayFromWalletAsync(invoice);
            }
        }

        return await ToResponseAsync(contract);
    }

    private async Task<Result<MonthlyServiceAttendanceItem>> MutateAsync(Guid customerId, Guid attendanceId, Action<MonthlyServiceAttendance> change)
    {
        var row = await _attendanceRepository.GetByIdAsync(attendanceId);
        if (row is null || row.CustomerId != customerId)
        {
            return Error.NotFound("MonthlyService.AttendanceNotFound", "Visit not found.");
        }

        // Through the engine: on a daily-wallet contract the day's payment or
        // refund commits with the change (e.g. a skip refunds the day).
        var changed = await _engine.ChangeAttendanceAsync(row, change);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        return _engine.ToItem(row, MonthlyServiceViewer.Customer);
    }

    private async Task<MonthlyServiceContractResponse> ToResponseAsync(MonthlyServiceContract contract)
    {
        var services = await _engine.ServiceNamesAsync([contract.ServiceIdSnapshot]);
        var addresses = await _engine.AddressesAsync([contract.AddressId]);
        var providers = contract.ProviderId is { } pid ? await _engine.ProvidersAsync([pid]) : new();

        var today = _engine.Today;
        var (monthStart, monthEnd) = MonthlyServiceEngine.MonthRange(today.Year, today.Month);
        var monthRows = await _attendanceRepository.ListByContractAsync(contract.Id, monthStart, monthEnd);
        var todayRow = monthRows.FirstOrDefault(r => r.Date == today);
        var unpaid = (await _invoiceRepository.ListByContractAsync(contract.Id)).Where(i => !i.IsPaid).Sum(i => i.Amount);
        var nextVisit = await _engine.NextVisitDateAsync(contract);

        return new MonthlyServiceContractResponse(
            contract.Id,
            contract.PlanId,
            contract.PlanNameSnapshot,
            services.GetValueOrDefault(contract.ServiceIdSnapshot, string.Empty),
            contract.BasisSnapshot,
            contract.HoursPerVisitSnapshot,
            contract.IncludedTasksSnapshot,
            contract.RatePerVisitSnapshot,
            MonthlyServiceEngine.Days(contract.Weekdays),
            MonthlyServiceTimeFormat.Format(contract.VisitStartTime),
            contract.StartDate,
            contract.EndDate,
            contract.Status,
            contract.PauseReason,
            contract.CustomerNote,
            MonthlyServiceEngine.ToAddress(addresses.GetValueOrDefault(contract.AddressId)),
            contract.ProviderId is { } id ? MonthlyServiceEngine.ToProviderSummary(providers.GetValueOrDefault(id)) : null,
            todayRow is null ? null : _engine.ToItem(todayRow, MonthlyServiceViewer.Customer),
            _engine.Summarize(monthRows, contract.RatePerVisitSnapshot),
            unpaid,
            contract.CreatedAtUtc,
            contract.CancelledAtUtc,
            contract.CancellationReason,
            contract.FrequencySnapshot,
            contract.TimesPerPeriodSnapshot,
            MonthlyServiceEngine.MonthDates(contract),
            nextVisit,
            contract.PersonsSnapshot,
            contract.PayFromWallet,
            contract.BillingMode);
    }

    private static Error ContractNotFound() =>
        Error.NotFound("MonthlyService.ContractNotFound", "Monthly service not found.");
}
