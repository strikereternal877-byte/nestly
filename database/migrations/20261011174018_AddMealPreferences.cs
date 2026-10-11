using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// docs/MEAL-SERVICE.md FOOD PREFERENCES and "keep this cook monthly":
    /// a cook booking item keeps its meals, people and food preferences; a
    /// monthly contract keeps the preferences and the cook asked for; and each
    /// customer's last preferences are saved to pre-fill the next booking.
    /// Additive only - every new column is nullable or defaults to 0.
    /// </summary>
    public partial class AddMealPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "meal_preferences_snapshot",
                table: "monthly_service_contract",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "preferred_provider_id",
                table: "monthly_service_contract",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "meal_preferences_snapshot",
                table: "booking_item",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "meals_mask",
                table: "booking_item",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "persons_snapshot",
                table: "booking_item",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "customer_meal_preference",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    preferences = table.Column<string>(type: "text", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_meal_preference", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_meal_preference_customer_id",
                table: "customer_meal_preference",
                column: "customer_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_meal_preference");

            migrationBuilder.DropColumn(
                name: "meal_preferences_snapshot",
                table: "monthly_service_contract");

            migrationBuilder.DropColumn(
                name: "preferred_provider_id",
                table: "monthly_service_contract");

            migrationBuilder.DropColumn(
                name: "meal_preferences_snapshot",
                table: "booking_item");

            migrationBuilder.DropColumn(
                name: "meals_mask",
                table: "booking_item");

            migrationBuilder.DropColumn(
                name: "persons_snapshot",
                table: "booking_item");
        }
    }
}
