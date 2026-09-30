using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Seed;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// docs/MONTHLY-SERVICE.md NOTIFICATIONS: seeds the seven Monthly Service
    /// notification types (SMS, email and push each - 21 rows) added to
    /// <see cref="NotificationTemplateSeedData.BuildDefaults"/>. Same
    /// incremental-seed shape as SeedProviderChangedNotificationTemplates:
    /// only the new event types' rows are inserted. Without it the dispatch
    /// would record "no_template" instead of sending anything.
    ///
    /// Data-only: the model is unchanged.
    /// </summary>
    public partial class SeedMonthlyServiceNotificationTemplates : Migration
    {
        private static readonly NotificationEventType[] EventTypes =
        {
            NotificationEventType.MonthlyProviderAssigned,
            NotificationEventType.MonthlyNewClient,
            NotificationEventType.MonthlyProviderLeave,
            NotificationEventType.MonthlyVisitSkipped,
            NotificationEventType.MonthlyInvoiceIssued,
            NotificationEventType.MonthlyServicePaused,
            NotificationEventType.MonthlyClientCancelled
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
                "DELETE FROM notification_template WHERE event_type IN ('MonthlyProviderAssigned','MonthlyNewClient','MonthlyProviderLeave','MonthlyVisitSkipped','MonthlyInvoiceIssued','MonthlyServicePaused','MonthlyClientCancelled');");
        }
    }
}
