using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderBankAccountConfiguration : IEntityTypeConfiguration<ProviderBankAccount>
{
    public void Configure(EntityTypeBuilder<ProviderBankAccount> builder)
    {
        builder.ToTable("provider_bank_account");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderId).IsRequired();
        builder.HasOne<Provider>()
            .WithMany()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Restrict);

        // One row per provider - a resubmission upserts this same row rather
        // than appending a new one (see ProviderBankAccount's doc comment).
        builder.HasIndex(x => x.ProviderId).IsUnique();

        builder.Property(x => x.AccountHolderName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.AccountNumber).IsRequired().HasMaxLength(20);
        builder.Property(x => x.IfscCode).IsRequired().HasMaxLength(11);
        builder.Property(x => x.BankName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.VerificationStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.VerifiedBy);
        builder.Property(x => x.VerifiedAt);
        builder.Property(x => x.RejectionReason).HasMaxLength(1000);
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.HasIndex(x => x.VerificationStatus);
    }
}
