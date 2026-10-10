using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nestly.Domain;

namespace Nestly.Infrastructure.Persistence.Configurations;

public class ProviderConfiguration : IEntityTypeConfiguration<Provider>
{
    public void Configure(EntityTypeBuilder<Provider> builder)
    {
        builder.ToTable("provider");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.LegalName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.ProviderType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Phone).IsRequired().HasMaxLength(20);
        builder.HasIndex(x => x.Phone).IsUnique();
        builder.Property(x => x.Email).HasMaxLength(200);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.OnboardingStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        // Task 243. Same precision as CustomerAddressConfiguration's
        // Latitude/Longitude - kept consistent across the two entities that
        // now carry real-world coordinates.
        builder.Property(x => x.Latitude).HasPrecision(9, 6);
        builder.Property(x => x.Longitude).HasPrecision(9, 6);

        // Task 268. Nullable: null exactly when the coordinates are, so a
        // never-located provider is distinguishable from one located long ago.
        builder.Property(x => x.LocationUpdatedAtUtc);

        // Task 293. A reference to an already-hosted image, sized like every
        // other media reference in this schema (ProviderKycDocument.FileRef).
        builder.Property(x => x.PhotoUrl).HasMaxLength(2000);
        builder.Property(x => x.PhotoModerationStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.PhotoModeratedByAdminUserId);
        builder.Property(x => x.PhotoModeratedAtUtc);
        builder.Property(x => x.PhotoModerationNote).HasMaxLength(1000);

        // Supports the admin moderation queue's only query - every provider
        // whose photo is still awaiting a verdict - which would otherwise
        // scan the whole provider table on every load of that screen.
        builder.HasIndex(x => x.PhotoModerationStatus);

        // Derived from the two columns above - a gate, not stored state.
        builder.Ignore(x => x.PublicPhotoUrl);

        // PROVIDER-REFERRAL.md, mirrors CustomerConfiguration's ReferralCode.
        builder.Property(x => x.ReferralCode).HasMaxLength(20);
        builder.HasIndex(x => x.ReferralCode).IsUnique().HasFilter("referral_code IS NOT NULL");

        // Provider Management UX pass, mirrors BookingConfiguration's StatusHistory navigation.
        builder.HasMany(x => x.StatusHistory)
            .WithOne()
            .HasForeignKey(x => x.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
