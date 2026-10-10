using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletTopUpAndPlanPauseFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pause_reason",
                table: "recurring_booking_plan",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "skip_ranges_used",
                table: "recurring_booking_plan",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "skip_until_date",
                table: "recurring_booking_plan",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "wallet_top_up",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    gateway_order_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    gateway_payment_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    wallet_ledger_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wallet_top_up", x => x.id);
                    table.ForeignKey(
                        name: "fk_wallet_top_up_customer_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customer",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_top_up_customer_id_created_at_utc",
                table: "wallet_top_up",
                columns: new[] { "customer_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_wallet_top_up_gateway_order_id",
                table: "wallet_top_up",
                column: "gateway_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wallet_top_up_status_created_at_utc",
                table: "wallet_top_up",
                columns: new[] { "status", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wallet_top_up");

            migrationBuilder.DropColumn(
                name: "pause_reason",
                table: "recurring_booking_plan");

            migrationBuilder.DropColumn(
                name: "skip_ranges_used",
                table: "recurring_booking_plan");

            migrationBuilder.DropColumn(
                name: "skip_until_date",
                table: "recurring_booking_plan");
        }
    }
}
