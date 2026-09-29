using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// docs/MONTHLY-SERVICE.md SCHEDULES: adds "N times a week / N times a
    /// month" scheduling (car wash) next to the original free-weekday
    /// schedule (maid). Additive only. Existing plans and contracts are
    /// backfilled as <c>Weekdays</c> with no date mask - exactly how they
    /// behaved before this column existed.
    /// </summary>
    public partial class AddMonthlyServiceFrequency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "frequency",
                table: "monthly_service_plan",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Weekdays");

            migrationBuilder.AddColumn<int>(
                name: "times_per_period",
                table: "monthly_service_plan",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "frequency_snapshot",
                table: "monthly_service_contract",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Weekdays");

            migrationBuilder.AddColumn<int>(
                name: "month_days_mask",
                table: "monthly_service_contract",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "times_per_period_snapshot",
                table: "monthly_service_contract",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "frequency",
                table: "monthly_service_plan");

            migrationBuilder.DropColumn(
                name: "times_per_period",
                table: "monthly_service_plan");

            migrationBuilder.DropColumn(
                name: "frequency_snapshot",
                table: "monthly_service_contract");

            migrationBuilder.DropColumn(
                name: "month_days_mask",
                table: "monthly_service_contract");

            migrationBuilder.DropColumn(
                name: "times_per_period_snapshot",
                table: "monthly_service_contract");
        }
    }
}
