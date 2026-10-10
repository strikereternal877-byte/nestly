using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompletionProofReviewStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                table: "booking_completion_proof",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_status",
                table: "booking_completion_proof",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Pending, not "": a proof submitted before this review system
                // existed was, in fact, never reviewed - and "" isn't a valid
                // CompletionProofReviewStatus in the first place, so any
                // pre-existing row would fail to deserialize on next read.
                defaultValue: "Pending");

            migrationBuilder.AddColumn<DateTime>(
                name: "reviewed_at_utc",
                table: "booking_completion_proof",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reviewed_by",
                table: "booking_completion_proof",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rejection_reason",
                table: "booking_completion_proof");

            migrationBuilder.DropColumn(
                name: "review_status",
                table: "booking_completion_proof");

            migrationBuilder.DropColumn(
                name: "reviewed_at_utc",
                table: "booking_completion_proof");

            migrationBuilder.DropColumn(
                name: "reviewed_by",
                table: "booking_completion_proof");
        }
    }
}
