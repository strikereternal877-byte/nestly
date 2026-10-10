using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPrepaidRecurringPlanAndPaymentGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "pending_prepayment_lead_booking_id",
                table: "recurring_booking_plan",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "pending_prepayment_through_date",
                table: "recurring_booking_plan",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "prepaid_cycles_paid",
                table: "recurring_booking_plan",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "prepaid_through_date",
                table: "recurring_booking_plan",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "prepaid_upfront",
                table: "recurring_booking_plan",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "payment_group_id",
                table: "payment_attempt",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payment_group",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway_order_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    visit_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    gateway_payment_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_group", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_group_booking_lead_booking_id",
                        column: x => x.lead_booking_id,
                        principalTable: "booking",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_group_customer_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customer",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_attempt_payment_group_id",
                table: "payment_attempt",
                column: "payment_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_group_customer_id",
                table: "payment_group",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_group_gateway_order_id",
                table: "payment_group",
                column: "gateway_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_group_lead_booking_id_created_at_utc",
                table: "payment_group",
                columns: new[] { "lead_booking_id", "created_at_utc" });

            migrationBuilder.AddForeignKey(
                name: "fk_payment_attempt_payment_groups_payment_group_id",
                table: "payment_attempt",
                column: "payment_group_id",
                principalTable: "payment_group",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_payment_attempt_payment_groups_payment_group_id",
                table: "payment_attempt");

            migrationBuilder.DropTable(
                name: "payment_group");

            migrationBuilder.DropIndex(
                name: "ix_payment_attempt_payment_group_id",
                table: "payment_attempt");

            migrationBuilder.DropColumn(
                name: "pending_prepayment_lead_booking_id",
                table: "recurring_booking_plan");

            migrationBuilder.DropColumn(
                name: "pending_prepayment_through_date",
                table: "recurring_booking_plan");

            migrationBuilder.DropColumn(
                name: "prepaid_cycles_paid",
                table: "recurring_booking_plan");

            migrationBuilder.DropColumn(
                name: "prepaid_through_date",
                table: "recurring_booking_plan");

            migrationBuilder.DropColumn(
                name: "prepaid_upfront",
                table: "recurring_booking_plan");

            migrationBuilder.DropColumn(
                name: "payment_group_id",
                table: "payment_attempt");
        }
    }
}
