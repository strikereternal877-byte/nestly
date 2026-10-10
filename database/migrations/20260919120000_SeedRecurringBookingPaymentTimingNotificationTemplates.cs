using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Seed;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// Recurring-booking payment-timing fix: seeds the
    /// RecurringBookingPaymentDue/RecurringAutoChargeScheduled
    /// notification_template rows added to
    /// <see cref="NotificationTemplateSeedData.BuildDefaults"/> for this phase.
    /// Same incremental-seed shape as
    /// 20260802091030_SeedSubscriptionNotificationTemplates.cs - only inserts
    /// the 2 new event types' rows (6 total: 2 event types x 3 channels); every
    /// other event type's rows already exist. Without this migration,
    /// RecurringBookingSchedulerService's dispatch calls for either new event
    /// type would silently record "no_template" failures on a live database.
    /// </summary>
    public partial class SeedRecurringBookingPaymentTimingNotificationTemplates : Migration
    {
        private static readonly NotificationEventType[] NewEventTypes =
        [
            NotificationEventType.RecurringBookingPaymentDue,
            NotificationEventType.RecurringAutoChargeScheduled
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
                "DELETE FROM notification_template WHERE event_type IN ('RecurringBookingPaymentDue', 'RecurringAutoChargeScheduled');");
        }
    }
}
