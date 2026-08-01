using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Seed;

#nullable disable

namespace Nestly.Infrastructure.Migrations
{
    /// <summary>
    /// Task 172: seeds the <c>notification_template</c> rows for
    /// <see cref="NotificationEventType.ReferralRegistered"/> and
    /// <see cref="NotificationEventType.ReferralRewardCredited"/> (Sms/Email/Push
    /// each, 6 rows total). The table and its schema already exist
    /// (20260731152427_AddNotificationTemplateManagement) - these two event
    /// types and their dispatch call sites were already wired up (tasks
    /// 161/163/165), but had no template rows, so
    /// <c>NotificationTemplateRenderer</c> was silently recording
    /// "no_template" failures for every referral notification. Filters
    /// <see cref="NotificationTemplateSeedData.BuildDefaults"/> down to just
    /// these two event types so this migration can never drift from the
    /// content <c>NotificationTemplateRendererTests</c> also reads, same
    /// "single source of truth" reasoning as the original seed migration.
    /// </summary>
    public partial class SeedReferralNotificationTemplates : Migration
    {
        private static readonly NotificationEventType[] SeededEventTypes =
        [
            NotificationEventType.ReferralRegistered,
            NotificationEventType.ReferralRewardCredited
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            string[] columns =
            {
                "id", "event_type", "channel", "template_key", "subject", "body",
                "is_active", "created_at_utc", "updated_at_utc", "updated_by_admin_user_id"
            };

            foreach (var row in NotificationTemplateSeedData.BuildDefaults().Where(r => SeededEventTypes.Contains(r.EventType)))
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
            foreach (var row in NotificationTemplateSeedData.BuildDefaults().Where(r => SeededEventTypes.Contains(r.EventType)))
            {
                migrationBuilder.DeleteData(
                    table: "notification_template",
                    keyColumn: "id",
                    keyValue: row.Id);
            }
        }
    }
}
