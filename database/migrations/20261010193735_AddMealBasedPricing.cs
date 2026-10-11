using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMealBasedPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "meal_options",
                table: "service",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "meals_mask",
                table: "monthly_service_contract",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "persons_snapshot",
                table: "monthly_service_contract",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "meal_options",
                table: "service");

            migrationBuilder.DropColumn(
                name: "meals_mask",
                table: "monthly_service_contract");

            migrationBuilder.DropColumn(
                name: "persons_snapshot",
                table: "monthly_service_contract");
        }
    }
}
