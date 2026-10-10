using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// Recurring-booking payment-timing fix: the customer's auto-charge
    /// consent on the plan, and per-booking attempt tracking for
    /// <c>RecurringOccurrenceAutoChargeJob</c>'s retry/backoff.
    /// </summary>
    public partial class AddRecurringBookingAutoCharge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_charge_enabled",
                table: "recurring_booking_plan",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "auto_charge_attempt_count",
                table: "booking",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<System.DateTime>(
                name: "last_auto_charge_attempt_at_utc",
                table: "booking",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_charge_enabled",
                table: "recurring_booking_plan");

            migrationBuilder.DropColumn(
                name: "auto_charge_attempt_count",
                table: "booking");

            migrationBuilder.DropColumn(
                name: "last_auto_charge_attempt_at_utc",
                table: "booking");
        }
    }
}
