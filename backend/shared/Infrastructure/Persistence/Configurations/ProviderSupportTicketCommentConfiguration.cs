using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderSupportTicketCommentConfiguration : IEntityTypeConfiguration<ProviderSupportTicketComment>
{
    public void Configure(EntityTypeBuilder<ProviderSupportTicketComment> builder)
    {
        builder.ToTable("provider_support_ticket_comment");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderSupportTicketId).IsRequired();
        builder.Property(x => x.AuthorType).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Comment).IsRequired().HasMaxLength(2000);
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasIndex(x => x.ProviderSupportTicketId);
    }
}
