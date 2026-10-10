using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderSupportTicketConfiguration : IEntityTypeConfiguration<ProviderSupportTicket>
{
    public void Configure(EntityTypeBuilder<ProviderSupportTicket> builder)
    {
        builder.ToTable("provider_support_ticket");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.Category).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Subject).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).IsRequired().HasMaxLength(4000);
        builder.Property(x => x.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.ResolutionSummary).HasMaxLength(2000);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();

        builder.HasMany(x => x.Comments)
            .WithOne()
            .HasForeignKey(x => x.ProviderSupportTicketId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.ProviderId);
        builder.HasIndex(x => x.Status);
    }
}
