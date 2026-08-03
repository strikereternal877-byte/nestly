using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderKycDocumentConfiguration : IEntityTypeConfiguration<ProviderKycDocument>
{
    public void Configure(EntityTypeBuilder<ProviderKycDocument> builder)
    {
        builder.ToTable("provider_kyc_document");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ProviderId);

        builder.Property(x => x.DocType).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.DocNumber).HasMaxLength(100);
        builder.Property(x => x.FileRef).IsRequired().HasMaxLength(1000);
        builder.Property(x => x.VerificationStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.VerifiedBy);
        builder.Property(x => x.VerifiedAt);
        builder.Property(x => x.SubmittedAt).IsRequired();
    }
}
