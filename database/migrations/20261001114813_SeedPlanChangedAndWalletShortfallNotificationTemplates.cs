using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Seed;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// Seeds the RecurringPlanChanged and RecurringWalletShortfall notification_template rows added to
    /// <see cref="NotificationTemplateSeedData.BuildDefaults"/> (6 rows: 2 event types x 3 channels).
    /// Same incremental-seed shape as 20261001095326_SeedPlanPausedAndWalletLowBalanceNotificationTemplates.cs -
    /// only the new event types' rows are inserted; every other event type's rows already exist on a live
    /// database. Without this migration the customer-action confirmation and the wallet-shortfall message would
    /// record "no_template" failures on a live database instead of telling the customer anything.
    ///
    /// Data-only: the model is unchanged, so the accompanying .Designer.cs snapshot matches the preceding
    /// migration's.
    /// </summary>
    public partial class SeedPlanChangedAndWalletShortfallNotificationTemplates : Migration
    {
        private static readonly NotificationEventType[] NewEventTypes =
        [
            NotificationEventType.RecurringPlanChanged,
            NotificationEventType.RecurringWalletShortfall
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            string[] columns =
            {
                "id", "event_type", "channel", "template_key", "subject", "body",
                "is_active", "created_at_utc", "updated_at_utc", "updated_by_admin_user_id"
            };

            foreach (var row in NotificationTemplateSeedData.BuildDefaults().Where(r => NewEventTypes.Contains(r.EventType)))
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
                "DELETE FROM notification_template WHERE event_type IN ('RecurringPlanChanged', 'RecurringWalletShortfall');");
        }
    }
}
