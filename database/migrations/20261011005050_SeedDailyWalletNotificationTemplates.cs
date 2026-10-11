using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Seed;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// docs/MONTHLY-SERVICE.md DAILY WALLET: seeds the five daily-wallet
    /// notification types (visit not booked - customer and professional,
    /// visit booked again, wallet low, month-end auto-pay failed), SMS, email
    /// and push each - 15 rows from <see cref="NotificationTemplateSeedData.BuildDefaults"/>.
    /// Same incremental shape as SeedMonthlyServiceNotificationTemplates: only
    /// the new event types' rows are inserted.
    ///
    /// Data-only: the model is unchanged.
    /// </summary>
    public partial class SeedDailyWalletNotificationTemplates : Migration
    {
        private static readonly NotificationEventType[] EventTypes =
        {
            NotificationEventType.MonthlyVisitNotBooked,
            NotificationEventType.MonthlyClientNotBooked,
            NotificationEventType.MonthlyVisitRebooked,
            NotificationEventType.MonthlyWalletLow,
            NotificationEventType.MonthlyAutopayFailed
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            string[] columns =
            {
                "id", "event_type", "channel", "template_key", "subject", "body",
                "is_active", "created_at_utc", "updated_at_utc", "updated_by_admin_user_id"
            };

            foreach (var row in NotificationTemplateSeedData.BuildDefaults().Where(r => EventTypes.Contains(r.EventType)))
            {
                migrationBuilder.InsertData(
                    table: "notification_template",
                    columns: columns,
                    values: new object[]
                    {
                        row.Id,
                        row.EventType.ToString(),
                        row.Channel.ToString(),
                        row.TemplateKey,
                        row.Subject,
                        row.Body,
                        true,
                        NotificationTemplateSeedData.SeedTimestampUtc,
                        NotificationTemplateSeedData.SeedTimestampUtc,
                        null
                    });
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM notification_template WHERE event_type IN ('MonthlyVisitNotBooked','MonthlyClientNotBooked','MonthlyVisitRebooked','MonthlyWalletLow','MonthlyAutopayFailed');");
        }
    }
}
