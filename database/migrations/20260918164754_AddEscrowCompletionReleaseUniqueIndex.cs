using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEscrowCompletionReleaseUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_platform_escrow_ledger_booking_id",
                table: "platform_escrow_ledger",
                column: "booking_id",
                unique: true,
                filter: "entry_type = 'Release' AND source_type = 'BookingCompleted'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_platform_escrow_ledger_booking_id",
                table: "platform_escrow_ledger");
        }
    }
}
