using Nestly.BuildingBlocks.Primitives;

namespace Nestly.Domain;

/// <summary>
/// An admin panel refresh-token session (SRS 12.1.2), mirroring
/// <see cref="CustomerSession"/> exactly for the admin identity. The access
/// token itself is stateless JWT and is not persisted; this row exists so a
/// refresh/logout can invalidate it.
/// </summary>
public class AdminSession : Entity<Guid>
{
    public Guid AdminUserId { get; private set; }
    public string RefreshTokenHash { get; private set; } = string.Empty;
    public DateTime IssuedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? DeviceInfo { get; private set; }
    public string? IpAddress { get; private set; }

    protected AdminSession() { }

    public AdminSession(Guid id, Guid adminUserId, string refreshTokenHash, DateTime issuedAt, DateTime expiresAt,
        string? deviceInfo = null, string? ipAddress = null) : base(id)
    {
        AdminUserId = adminUserId;
        RefreshTokenHash = refreshTokenHash ?? throw new ArgumentNullException(nameof(refreshTokenHash));
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt > issuedAt
            ? expiresAt
            : throw new ArgumentOutOfRangeException(nameof(expiresAt));
        DeviceInfo = deviceInfo;
        IpAddress = ipAddress;
    }

    public bool IsActive(DateTime asOfUtc) => RevokedAt is null && asOfUtc < ExpiresAt;

    public void Revoke() => RevokedAt = DateTime.UtcNow;
}
