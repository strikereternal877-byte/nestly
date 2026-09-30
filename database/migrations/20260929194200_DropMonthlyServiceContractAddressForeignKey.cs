using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// Lets a customer delete an address once the monthly service there is
    /// cancelled: the contract keeps its address id for history but no
    /// longer holds a foreign key that would make the delete fail. A running
    /// service still blocks the delete - in CustomerAddressService, with a
    /// clear message instead of a database error. The index stays.
    /// </summary>
    public partial class DropMonthlyServiceContractAddressForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_monthly_service_contract_customer_address_address_id",
                table: "monthly_service_contract");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "fk_monthly_service_contract_customer_address_address_id",
                table: "monthly_service_contract",
                column: "address_id",
                principalTable: "customer_address",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
