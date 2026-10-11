using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain.Events;

// docs/MONTHLY-SERVICE.md NOTIFICATIONS - the moments a customer or a
// professional should hear about without having to open the app.

/// <summary>A professional was assigned to (or replaced on) a monthly engagement. Tells the customer who is coming, and the professional about their new home.</summary>
public sealed record MonthlyServiceProviderAssignedEvent(Guid ContractId, Guid CustomerId, Guid ProviderId, bool IsReplacement, Guid? PreviousProviderId = null) : DomainEvent;

/// <summary>The professional marked leave for a day - the customer should not wait for them.</summary>
public sealed record MonthlyServiceLeaveMarkedEvent(Guid AttendanceId, Guid ContractId, Guid CustomerId, Guid ProviderId, DateOnly Date) : DomainEvent;

/// <summary>The customer skipped a day - the professional should not go.</summary>
public sealed record MonthlyServiceVisitSkippedEvent(Guid AttendanceId, Guid ContractId, Guid CustomerId, Guid ProviderId, DateOnly Date) : DomainEvent;

/// <summary>A month-end bill was issued.</summary>
public sealed record MonthlyServiceInvoiceIssuedEvent(Guid InvoiceId, Guid ContractId, Guid CustomerId, decimal Amount, DateOnly PeriodStart, DateOnly DueDate) : DomainEvent;

/// <summary>A contract was paused because a bill stayed unpaid past its grace period.</summary>
public sealed record MonthlyServicePausedForNonPaymentEvent(Guid ContractId, Guid CustomerId) : DomainEvent;

/// <summary>A contract was cancelled (by the customer or an admin) - its professional should stop going.</summary>
public sealed record MonthlyServiceCancelledEvent(Guid ContractId, Guid CustomerId, Guid? ProviderId) : DomainEvent;

// Daily wallet billing (docs/MONTHLY-SERVICE.md DAILY WALLET) - the
// Country Delight model: each day's visit is paid from the wallet before it
// happens, and is only on when the wallet covered it.

/// <summary>A day's visit could not be paid from the wallet, so it is not on: the customer should add money, the professional should not go.</summary>
public sealed record MonthlyServiceVisitNotBookedEvent(Guid AttendanceId, Guid ContractId, Guid CustomerId, Guid ProviderId, DateOnly Date, decimal Amount) : DomainEvent;

/// <summary>A day that was not booked for low balance was paid and is on again - the professional should go after all.</summary>
public sealed record MonthlyServiceVisitRebookedEvent(Guid AttendanceId, Guid ContractId, Guid CustomerId, Guid ProviderId, DateOnly Date) : DomainEvent;

/// <summary>The wallet will not cover the next days' visits - an early warning, at most one a day per customer.</summary>
public sealed record MonthlyServiceWalletLowEvent(Guid ContractId, Guid CustomerId, decimal Balance, decimal NeededForNextDays, int VisitsCovered) : DomainEvent;

/// <summary>A month-end bill set to auto-pay from the wallet could not be paid - the balance was short.</summary>
public sealed record MonthlyServiceAutopayFailedEvent(Guid InvoiceId, Guid ContractId, Guid CustomerId, decimal Amount, decimal Balance) : DomainEvent;
