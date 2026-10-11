using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyWalletBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "monthly_discount_percent",
                table: "monthly_service_plan",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "billing_mode",
                table: "monthly_service_contract",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "MonthEnd");

            migrationBuilder.AddColumn<decimal>(
                name: "prepaid_amount",
                table: "monthly_service_attendance",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "prepaid_refunded_at_utc",
                table: "monthly_service_attendance",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "prepaid_wallet_entry_id",
                table: "monthly_service_attendance",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "monthly_discount_percent",
                table: "monthly_service_plan");

            migrationBuilder.DropColumn(
                name: "billing_mode",
                table: "monthly_service_contract");

            migrationBuilder.DropColumn(
                name: "prepaid_amount",
                table: "monthly_service_attendance");

            migrationBuilder.DropColumn(
                name: "prepaid_refunded_at_utc",
                table: "monthly_service_attendance");

            migrationBuilder.DropColumn(
                name: "prepaid_wallet_entry_id",
                table: "monthly_service_attendance");
        }
    }
}
