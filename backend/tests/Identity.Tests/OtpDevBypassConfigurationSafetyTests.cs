using System.Text.Json;
using FluentAssertions;

namespace Nestly.Identity.Tests;

/// <summary>
/// <see cref="Nestly.Infrastructure.Options.OtpOptions.AllowDevBypass"/> lets
/// OtpService/ProviderOtpService accept a fixed well-known code instead of the
/// real one - safe only because it is set nowhere but
/// <c>appsettings.Development.json</c>. That guarantee currently rests on
/// convention (a doc comment and an inline "_comment" note) rather than
/// anything enforced, which is exactly the kind of thing that is invisible
/// until the day it is not true - this test makes it load-bearing instead.
///
/// FALSIFIABILITY: adding <c>"AllowDevBypass": true</c> to any
/// non-Development appsettings file for consumer-api or provider-api fails
/// this test.
/// </summary>
public class OtpDevBypassConfigurationSafetyTests
{
    private const string AllowDevBypassKey = "AllowDevBypass";
    private const string OtpSection = "Otp";

    /// <summary>
    /// Every appsettings file for the two APIs that issue OTPs, relative to
    /// the repo root, except appsettings.Development.json itself - the one
    /// place the bypass is allowed to live.
    /// </summary>
    private static readonly string[] NonDevelopmentAppSettings =
    [
        Path.Combine("backend", "consumer-api", "ConsumerApi", "appsettings.json"),
        Path.Combine("backend", "consumer-api", "ConsumerApi", "appsettings.Testing.json"),
        Path.Combine("backend", "consumer-api", "ConsumerApi", "appsettings.Staging.json"),
        Path.Combine("backend", "consumer-api", "ConsumerApi", "appsettings.Production.json"),
        Path.Combine("backend", "provider-api", "ProviderApi", "appsettings.json"),
        Path.Combine("backend", "provider-api", "ProviderApi", "appsettings.Testing.json"),
        Path.Combine("backend", "provider-api", "ProviderApi", "appsettings.Staging.json"),
        Path.Combine("backend", "provider-api", "ProviderApi", "appsettings.Production.json"),
    ];

    private static readonly string[] DevelopmentAppSettings =
    [
        Path.Combine("backend", "consumer-api", "ConsumerApi", "appsettings.Development.json"),
        Path.Combine("backend", "provider-api", "ProviderApi", "appsettings.Development.json"),
    ];

    [Theory]
    [MemberData(nameof(NonDevelopmentAppSettingsData))]
    public void AllowDevBypass_never_appears_outside_Development_config(string relativePath)
    {
        Dictionary<string, JsonElement>? section = ReadOtpSectionIfPresent(relativePath);

        // No "Otp" section at all (the norm for these files - Pepper is a
        // secret that must come from config/user-secrets/environment, never
        // a literal here) is the safe case, but explicitly computed rather
        // than left to `section?.` short-circuiting the assertion itself:
        // that pattern was tried first and silently never ran the check on
        // every file in this list, which a real regression would have sailed
        // straight through.
        bool hasBypassKey = section?.ContainsKey(AllowDevBypassKey) ?? false;

        hasBypassKey.Should().BeFalse(
            $"{relativePath} is not appsettings.Development.json - AllowDevBypass letting a fixed code " +
            "pass OTP verification must never be reachable outside local development, and its absence " +
            "here (rather than a false value) is what makes it impossible to enable by accident.");
    }

    [Theory]
    [MemberData(nameof(DevelopmentAppSettingsData))]
    public void AllowDevBypass_is_still_wired_in_Development_config(string relativePath)
    {
        Dictionary<string, JsonElement> section = ReadOtpSectionIfPresent(relativePath)
            ?? throw new InvalidOperationException($"{relativePath} must carry an \"{OtpSection}\" section.");

        section.Should().ContainKey(AllowDevBypassKey,
            $"{relativePath} is where local OTP-gated testing (signup, login) depends on the fixed " +
            "bypass code - if this key disappears here, local testing silently loses the capability " +
            "documented in docs/UI-GUIDE.md and this session's own work.");

        section[AllowDevBypassKey].GetBoolean().Should().BeTrue(
            $"{relativePath}'s AllowDevBypass must be true - a false value here defeats the point of the " +
            "flag existing in the Development file at all.");
    }

    public static IEnumerable<object[]> NonDevelopmentAppSettingsData() =>
        NonDevelopmentAppSettings.Select(path => new object[] { path });

    public static IEnumerable<object[]> DevelopmentAppSettingsData() =>
        DevelopmentAppSettings.Select(path => new object[] { path });

    private static Dictionary<string, JsonElement>? ReadOtpSectionIfPresent(string relativePath)
    {
        string fullPath = Path.Combine(FindRepoRoot(), relativePath);
        File.Exists(fullPath).Should().BeTrue($"the appsettings file this test pins against must exist at {fullPath}");

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(fullPath),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        if (!document.RootElement.TryGetProperty(OtpSection, out JsonElement section))
        {
            return null;
        }

        // .Clone() so each JsonElement stays valid after `document` (and its
        // backing buffer) is disposed at the end of this method - unlike
        // AutoAssignmentConfigurationReachTests' equivalent helper, this one
        // reads a value off an element (GetBoolean() below), not just names.
        return section.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nestly.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull(
            "this test must run from within the Nestly repo (a Nestly.sln must be findable above the test binary's output directory)");
        return directory!.FullName;
    }
}
