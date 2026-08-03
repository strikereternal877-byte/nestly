namespace Nestly.Domain.NestlyCoins;

/// <summary>
/// Which side of the marketplace a <see cref="NestlyCoinsProgramConfig"/>
/// row governs (docs/NESTLY-COINS.md GUIDELINES #5: "one coins program per
/// side" - customer reordering and provider job-completion are different
/// behaviors being incentivized, with independent earn rates/minimums, not
/// the same lever).
/// </summary>
public enum NestlyCoinsAudience
{
    Customer,
    Provider
}
