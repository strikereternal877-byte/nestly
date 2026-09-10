using FluentValidation;

namespace Nestly.Application.BookingManagement;

/// <summary>Bounds paging and the date ranges so a caller cannot request an unbounded/inverted range (task 115a).</summary>
public class AdminBookingSearchRequestValidator : AbstractValidator<AdminBookingSearchRequest>
{
    public const int MaxPageSize = 100;

    public AdminBookingSearchRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);

        RuleFor(x => x.SlotDateTo)
            .GreaterThanOrEqualTo(x => x.SlotDateFrom!.Value)
            .When(x => x.SlotDateFrom.HasValue && x.SlotDateTo.HasValue)
            .WithMessage("Slot date-to must be on or after slot date-from.");

        RuleFor(x => x.CreatedToUtc)
            .GreaterThanOrEqualTo(x => x.CreatedFromUtc!.Value)
            .When(x => x.CreatedFromUtc.HasValue && x.CreatedToUtc.HasValue)
            .WithMessage("Created-to date must be on or after the created-from date.");
    }
}

/// <summary>Restricts the generic status endpoint to a defined enum value and a bounded reason (task 115d).</summary>
public class AdminBookingStatusUpdateRequestValidator : AbstractValidator<AdminBookingStatusUpdateRequest>
{
    public AdminBookingStatusUpdateRequestValidator()
    {
        RuleFor(x => x.NewStatus).IsInEnum();
        RuleFor(x => x.Reason).MaximumLength(1000).When(x => x.Reason is not null);
    }
}

/// <summary>A cancellation reason is always required (SRS 11.14.2), matching <c>CancelBookingRequestValidator</c>.</summary>
public class AdminCancelBookingRequestValidator : AbstractValidator<AdminCancelBookingRequest>
{
    public AdminCancelBookingRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
        RuleFor(x => x.InternalNotes).MaximumLength(2000).When(x => x.InternalNotes is not null);
    }
}

/// <summary>Matching <c>RescheduleBookingRequestValidator</c>'s rules for the admin-initiated equivalent (task 117b).</summary>
public class AdminRescheduleBookingRequestValidator : AbstractValidator<AdminRescheduleBookingRequest>
{
    public AdminRescheduleBookingRequestValidator()
    {
        RuleFor(x => x.LocalityId).NotEmpty();
        RuleFor(x => x.SlotWindowId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(500).When(x => x.Reason is not null);
    }
}

/// <summary>A positive amount is required for a partial refund (task 75c's own rule); ignored - and so not required - for a full refund (SRS 12.13.2).</summary>
public class AdminRefundRequestValidator : AbstractValidator<AdminRefundRequest>
{
    public AdminRefundRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Amount)
            .NotNull().GreaterThan(0m)
            .When(x => !x.IsFullRefund)
            .WithMessage("A positive amount is required for a partial refund.");
    }
}

/// <summary>A reference is always required so a manual payment can be reconciled later (row 25, docs/OPEN-FIXES-FEATURES.csv).</summary>
public class AdminManualPaymentRequestValidator : AbstractValidator<AdminManualPaymentRequest>
{
    public AdminManualPaymentRequestValidator()
    {
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Reference).NotEmpty().MaximumLength(200);
    }
}

/// <summary>Bounds paging, matching <see cref="AdminBookingSearchRequestValidator"/>'s rules (row "Unassigned and at-risk queue", docs/OPEN-FIXES-FEATURES.csv).</summary>
public class AdminUnassignedAtRiskBookingRequestValidator : AbstractValidator<AdminUnassignedAtRiskBookingRequest>
{
    public const int MaxPageSize = 100;

    public AdminUnassignedAtRiskBookingRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);
    }
}
