using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Application;
using Nestly.Domain;
using Nestly.Domain.MonthlyService;

namespace Nestly.Infrastructure.Persistence.Configurations;

/// <summary>
/// Task lists are short (at most <see cref="MonthlyServicePlan.MaxIncludedTasks"/>
/// entries of plain text) and only ever read whole, so they are stored as one
/// newline-separated text column rather than a child table or a jsonb column -
/// portable to the SQLite test provider as well as Postgres.
/// </summary>
internal static class TaskListColumn
{
    public static readonly ValueComparer<IReadOnlyList<string>> Comparer = new(
        (a, b) => (a ?? Array.Empty<string>()).SequenceEqual(b ?? Array.Empty<string>()),
        v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
        v => v.ToList());

    public static string ToColumn(IReadOnlyList<string> tasks) => string.Join('\n', tasks);

    public static IReadOnlyList<string> FromColumn(string column) =>
        string.IsNullOrEmpty(column) ? new List<string>() : column.Split('\n').ToList();
}

public class MonthlyServicePlanConfiguration : IEntityTypeConfiguration<MonthlyServicePlan>
{
    public void Configure(EntityTypeBuilder<MonthlyServicePlan> builder)
    {
        builder.ToTable("monthly_service_plan");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ServiceId).IsRequired();
        builder.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.CityId).IsRequired();
        builder.HasOne<City>().WithMany().HasForeignKey(x => x.CityId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(150);
        builder.HasIndex(x => x.Name).IsUnique();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.Property(x => x.Basis).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.HoursPerVisit).HasPrecision(4, 2);
        builder.Property(x => x.IncludedTasks)
            .HasConversion(v => TaskListColumn.ToColumn(v), v => TaskListColumn.FromColumn(v), TaskListColumn.Comparer)
            .HasColumnName("included_tasks")
            .UsePropertyAccessMode(PropertyAccessMode.Property)
            .IsRequired()
            .HasMaxLength(2100);
        builder.Property(x => x.RatePerVisit).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.CommissionPercent).IsRequired().HasPrecision(5, 2);
        builder.Property(x => x.Frequency).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.TimesPerPeriod);
        builder.Property(x => x.IsActive).IsRequired();
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedByAdminUserId);

        builder.HasIndex(x => new { x.CityId, x.IsActive });
    }
}

public class MonthlyServiceContractConfiguration : IEntityTypeConfiguration<MonthlyServiceContract>
{
    public void Configure(EntityTypeBuilder<MonthlyServiceContract> builder)
    {
        builder.ToTable("monthly_service_contract");
        builder.HasKey(x => x.Id);
        builder.Ignore(x => x.VisitDuration);

        builder.Property(x => x.CustomerId).IsRequired();
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);

        // Traceability only - every term is snapshotted (see the entity's doc comment).
        builder.Property(x => x.PlanId).IsRequired();
        builder.Property(x => x.PlanNameSnapshot).IsRequired().HasMaxLength(150);
        builder.Property(x => x.ServiceIdSnapshot).IsRequired();
        builder.Property(x => x.CityIdSnapshot).IsRequired();
        builder.Property(x => x.BasisSnapshot).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.HoursPerVisitSnapshot).HasPrecision(4, 2);
        builder.Property(x => x.IncludedTasksSnapshot)
            .HasConversion(v => TaskListColumn.ToColumn(v), v => TaskListColumn.FromColumn(v), TaskListColumn.Comparer)
            .HasColumnName("included_tasks_snapshot")
            .UsePropertyAccessMode(PropertyAccessMode.Property)
            .IsRequired()
            .HasMaxLength(2100);
        builder.Property(x => x.RatePerVisitSnapshot).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.CommissionPercentSnapshot).IsRequired().HasPrecision(5, 2);

        // Not a foreign key on purpose: a customer may delete an address once
        // the service there is cancelled (a running one blocks the delete in
        // CustomerAddressService), and the cancelled contract's history must
        // survive that. Readers treat a missing address as "no longer on file".
        builder.Property(x => x.AddressId).IsRequired();
        builder.HasIndex(x => x.AddressId);

        builder.Property(x => x.FrequencySnapshot).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.TimesPerPeriodSnapshot);
        builder.Property(x => x.Weekdays).IsRequired().HasConversion<int>();
        builder.Property(x => x.MonthDaysMask).IsRequired();
        builder.Property(x => x.VisitStartTime).IsRequired();
        builder.Property(x => x.StartDate).IsRequired();
        builder.Property(x => x.EndDate);

        builder.Property(x => x.ProviderId);
        builder.HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.ProviderAssignedAtUtc);

        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.PauseReason).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.CustomerNote).HasMaxLength(MonthlyServiceContract.MaxNoteLength);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();
        builder.Property(x => x.CancelledAtUtc);
        builder.Property(x => x.CancellationReason).HasMaxLength(MonthlyServiceContract.MaxNoteLength);

        builder.HasIndex(x => x.CustomerId);
        builder.HasIndex(x => new { x.ProviderId, x.Status });
        builder.HasIndex(x => x.Status);
    }
}

public class MonthlyServiceAttendanceConfiguration : IEntityTypeConfiguration<MonthlyServiceAttendance>
{
    public void Configure(EntityTypeBuilder<MonthlyServiceAttendance> builder)
    {
        builder.ToTable("monthly_service_attendance");
        builder.HasKey(x => x.Id);
        builder.Ignore(x => x.VisitStartLocal);
        builder.Ignore(x => x.IsInvoiced);

        builder.Property(x => x.ContractId).IsRequired();
        builder.HasOne<MonthlyServiceContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.CustomerId).IsRequired();
        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.Date).IsRequired();
        builder.Property(x => x.VisitStartTime).IsRequired();
        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.DayCode).IsRequired().HasMaxLength(MonthlyServiceAttendance.DayCodeLength);
        builder.Property(x => x.CheckedInAtUtc);
        builder.Property(x => x.CheckedOutAtUtc);
        builder.Property(x => x.CheckInLatitude).HasPrecision(9, 6);
        builder.Property(x => x.CheckInLongitude).HasPrecision(9, 6);
        builder.Property(x => x.MarkedBy).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.MarkedAtUtc);
        builder.Property(x => x.Note).HasMaxLength(MonthlyServiceAttendance.MaxNoteLength);
        builder.Property(x => x.DisputeStatus).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.DisputeReason).HasMaxLength(MonthlyServiceAttendance.MaxNoteLength);
        builder.Property(x => x.DisputeRaisedAtUtc);
        builder.Property(x => x.DisputeResolvedAtUtc);
        builder.Property(x => x.DisputeResolutionNote).HasMaxLength(MonthlyServiceAttendance.MaxNoteLength);
        builder.Property(x => x.InvoiceId);
        builder.HasOne<MonthlyServiceInvoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        // One register line per contract per day - also what makes the
        // materialization job safe to re-run.
        builder.HasIndex(x => new { x.ContractId, x.Date }).IsUnique();
        builder.HasIndex(x => new { x.ProviderId, x.Date });
        builder.HasIndex(x => new { x.Status, x.Date });
        builder.HasIndex(x => x.DisputeStatus);
    }
}

public class MonthlyServiceInvoiceConfiguration : IEntityTypeConfiguration<MonthlyServiceInvoice>
{
    public void Configure(EntityTypeBuilder<MonthlyServiceInvoice> builder)
    {
        builder.ToTable("monthly_service_invoice");
        builder.HasKey(x => x.Id);
        builder.Ignore(x => x.IsPaid);

        builder.Property(x => x.ContractId).IsRequired();
        builder.HasOne<MonthlyServiceContract>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.CustomerId).IsRequired();
        builder.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.PeriodStart).IsRequired();
        builder.Property(x => x.PeriodEnd).IsRequired();
        builder.Property(x => x.PresentCount).IsRequired();
        builder.Property(x => x.CustomerUnavailableCount).IsRequired();
        builder.Property(x => x.CustomerSkippedCount).IsRequired();
        builder.Property(x => x.ProviderLeaveCount).IsRequired();
        builder.Property(x => x.AbsentCount).IsRequired();
        builder.Property(x => x.BillableVisits).IsRequired();
        builder.Property(x => x.RatePerVisit).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.Amount).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.CommissionPercent).IsRequired().HasPrecision(5, 2);
        builder.Property(x => x.CommissionAmount).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.ProviderNetAmount).IsRequired().HasPrecision(12, 2);
        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.IssuedAtUtc).IsRequired();
        builder.Property(x => x.DueDate).IsRequired();
        builder.Property(x => x.PaidAtUtc);
        builder.Property(x => x.PaymentMethod).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.PaymentReference).HasMaxLength(MonthlyServiceInvoice.MaxReferenceLength);
        builder.Property(x => x.RecordedByAdminUserId);

        builder.HasIndex(x => new { x.ContractId, x.PeriodStart, x.ProviderId }).IsUnique();
        builder.HasIndex(x => x.CustomerId);
        builder.HasIndex(x => new { x.Status, x.DueDate });
    }
}
