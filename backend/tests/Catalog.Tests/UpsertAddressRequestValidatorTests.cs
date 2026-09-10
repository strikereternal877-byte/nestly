using FluentAssertions;
using Nestly.Application.Addresses;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Covers task 334: the pincode rule here and customer-web's AddressForm
/// (<c>z.string().regex(/^\d{6}$/)</c>) disagreed - the backend accepted any
/// non-empty string up to 12 characters, so a value the form rejected could
/// still be persisted through any other client, and a serviceable area whose
/// pincode did not match the form's shape was unaddressable from the web app.
/// These tests pin the backend to the stricter of the two.
/// </summary>
public sealed class UpsertAddressRequestValidatorTests
{
    private readonly UpsertAddressRequestValidator _validator = new();

    private static UpsertAddressRequest Request(string pincode) => new(
        Label: "Home",
        Line1: "221B Baker Street",
        Line2: null,
        Landmark: null,
        Pincode: pincode,
        City: "Bengaluru",
        State: "Karnataka",
        Latitude: 12.9716m,
        Longitude: 77.5946m,
        ContactName: "Test Customer",
        ContactMobile: "+919876543210",
        IsDefault: false);

    [Fact]
    public void Six_digit_pincode_passes()
    {
        _validator.Validate(Request("560034")).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]           // NotEmpty
    [InlineData("56003")]      // too short
    [InlineData("5600345")]    // too long
    [InlineData("56003A")]     // not all digits
    [InlineData("ABC123")]     // the shape the old MaximumLength(12) rule let through
    [InlineData(" 560034")]    // leading whitespace
    [InlineData("560034 ")]    // trailing whitespace
    public void Non_six_digit_pincode_fails(string pincode)
    {
        _validator.Validate(Request(pincode)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Rejection_message_matches_the_one_ProfileValidators_already_uses()
    {
        // The two validators guard the same concept; a customer should not see
        // two different explanations for the same rule depending on which
        // screen they are on.
        var result = _validator.Validate(Request("ABC123"));

        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("Pincode must be 6 digits");
    }

    private static UpsertAddressRequest RequestWithLine1(string line1) =>
        Request("560034") with { Line1 = line1 };

    /// <summary>
    /// Address snapshot fix: junk like "kuchh ni" (filler text, no address
    /// content) reached a live booking's stored address unvalidated. These
    /// tests cover the structural screen that replaces the old "any non-empty
    /// string up to 300 chars" rule - see BeMeaningfulAddressLine's doc
    /// comment for what it deliberately does not attempt to catch.
    /// </summary>
    [Theory]
    [InlineData("")]           // NotEmpty
    [InlineData("   ")]        // whitespace only
    [InlineData("abc")]        // shorter than the minimum
    [InlineData(".....")]      // mashed/placeholder run, no letters or digits
    [InlineData("aaaaaaaaaa")] // repeated-character run
    [InlineData("!!!!!!!!!!")]  // punctuation only, no alphanumeric content
    public void Junk_address_line_1_fails(string line1)
    {
        _validator.Validate(RequestWithLine1(line1)).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("221B Baker Street")]
    [InlineData("Flat 4B, Sunrise Apartments")]
    [InlineData("12345 Main Road")]
    public void Real_address_line_1_passes(string line1)
    {
        _validator.Validate(RequestWithLine1(line1)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Empty_optional_line_2_still_passes()
    {
        var request = Request("560034") with { Line2 = "" };
        _validator.Validate(request).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Junk_line_2_fails_when_provided()
    {
        var request = Request("560034") with { Line2 = "....." };
        _validator.Validate(request).IsValid.Should().BeFalse();
    }
}
