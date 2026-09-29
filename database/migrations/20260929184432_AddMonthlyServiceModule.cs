using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// Creates the Monthly Service module's four tables (docs/MONTHLY-SERVICE.md):
    /// <c>monthly_service_plan</c>, <c>monthly_service_contract</c>,
    /// <c>monthly_service_attendance</c> and <c>monthly_service_invoice</c>.
    /// Purely additive - no existing table is touched. The new
    /// <c>ProviderEarningSourceType.MonthlyServiceInvoice</c> value needs no
    /// schema change (the column is a string of max length 30).
    /// </summary>
    public partial class AddMonthlyServiceModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "monthly_service_contract",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_name_snapshot = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    service_id_snapshot = table.Column<Guid>(type: "uuid", nullable: false),
                    city_id_snapshot = table.Column<Guid>(type: "uuid", nullable: false),
                    basis_snapshot = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    hours_per_visit_snapshot = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: true),
                    included_tasks_snapshot = table.Column<string>(type: "character varying(2100)", maxLength: 2100, nullable: false),
                    rate_per_visit_snapshot = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    commission_percent_snapshot = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    address_id = table.Column<Guid>(type: "uuid", nullable: false),
                    weekdays = table.Column<int>(type: "integer", nullable: false),
                    visit_start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_assigned_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    pause_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    customer_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cancelled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monthly_service_contract", x => x.id);
                    table.ForeignKey(
                        name: "fk_monthly_service_contract_customer_address_address_id",
                        column: x => x.address_id,
                        principalTable: "customer_address",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monthly_service_contract_customer_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customer",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monthly_service_contract_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "provider",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monthly_service_plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    city_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    basis = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    hours_per_visit = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: true),
                    included_tasks = table.Column<string>(type: "character varying(2100)", maxLength: 2100, nullable: false),
                    rate_per_visit = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    commission_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by_admin_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monthly_service_plan", x => x.id);
                    table.ForeignKey(
                        name: "fk_monthly_service_plan_city_city_id",
                        column: x => x.city_id,
                        principalTable: "city",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monthly_service_plan_service_service_id",
                        column: x => x.service_id,
                        principalTable: "service",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monthly_service_invoice",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    present_count = table.Column<int>(type: "integer", nullable: false),
                    customer_unavailable_count = table.Column<int>(type: "integer", nullable: false),
                    customer_skipped_count = table.Column<int>(type: "integer", nullable: false),
                    provider_leave_count = table.Column<int>(type: "integer", nullable: false),
                    absent_count = table.Column<int>(type: "integer", nullable: false),
                    billable_visits = table.Column<int>(type: "integer", nullable: false),
                    rate_per_visit = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    commission_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    commission_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    provider_net_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    payment_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    payment_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    recorded_by_admin_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monthly_service_invoice", x => x.id);
                    table.ForeignKey(
                        name: "fk_monthly_service_invoice_customer_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customer",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monthly_service_invoice_monthly_service_contract_contract_id",
                        column: x => x.contract_id,
                        principalTable: "monthly_service_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monthly_service_invoice_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "provider",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monthly_service_attendance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    visit_start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    day_code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    checked_in_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    checked_out_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    check_in_latitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    check_in_longitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    marked_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    marked_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    dispute_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    dispute_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    dispute_raised_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    dispute_resolved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    dispute_resolution_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monthly_service_attendance", x => x.id);
                    table.ForeignKey(
                        name: "fk_monthly_service_attendance_monthly_service_contracts_contra",
                        column: x => x.contract_id,
                        principalTable: "monthly_service_contract",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monthly_service_attendance_monthly_service_invoices_invoice",
                        column: x => x.invoice_id,
                        principalTable: "monthly_service_invoice",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_monthly_service_attendance_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "provider",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_attendance_contract_id_date",
                table: "monthly_service_attendance",
                columns: new[] { "contract_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_attendance_dispute_status",
                table: "monthly_service_attendance",
                column: "dispute_status");

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_attendance_invoice_id",
                table: "monthly_service_attendance",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_attendance_provider_id_date",
                table: "monthly_service_attendance",
                columns: new[] { "provider_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_attendance_status_date",
                table: "monthly_service_attendance",
                columns: new[] { "status", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_contract_address_id",
                table: "monthly_service_contract",
                column: "address_id");

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_contract_customer_id",
                table: "monthly_service_contract",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_contract_provider_id_status",
                table: "monthly_service_contract",
                columns: new[] { "provider_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_contract_status",
                table: "monthly_service_contract",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_invoice_contract_id_period_start_provider_id",
                table: "monthly_service_invoice",
                columns: new[] { "contract_id", "period_start", "provider_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_invoice_customer_id",
                table: "monthly_service_invoice",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_invoice_provider_id",
                table: "monthly_service_invoice",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_invoice_status_due_date",
                table: "monthly_service_invoice",
                columns: new[] { "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_plan_city_id_is_active",
                table: "monthly_service_plan",
                columns: new[] { "city_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_plan_name",
                table: "monthly_service_plan",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_monthly_service_plan_service_id",
                table: "monthly_service_plan",
                column: "service_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monthly_service_attendance");

            migrationBuilder.DropTable(
                name: "monthly_service_plan");

            migrationBuilder.DropTable(
                name: "monthly_service_invoice");

            migrationBuilder.DropTable(
                name: "monthly_service_contract");
        }
    }
}
