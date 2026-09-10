using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSlotWindowCityActiveIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_slot_window_city_id",
                table: "slot_window");

            migrationBuilder.CreateIndex(
                name: "ix_slot_window_city_id_is_active",
                table: "slot_window",
                columns: new[] { "city_id", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_slot_window_city_id_is_active",
                table: "slot_window");

            migrationBuilder.CreateIndex(
                name: "ix_slot_window_city_id",
                table: "slot_window",
                column: "city_id");
        }
    }
}
