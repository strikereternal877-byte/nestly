using System.Linq;
using System.Text.RegularExpressions;
using FluentValidation;

namespace Nestly.Application.Addresses;

public class UpsertAddressRequestValidator : AbstractValidator<UpsertAddressRequest>
{
    /// <summary>
    /// A customer-typed address line must have at least this many
    /// non-whitespace characters to be worth storing (task: "Address
    /// snapshot" validation fix). Deliberately low - this only screens out
    /// the obviously-too-short case, not a real minimum street-address
    /// length, which varies too much to police.
    /// </summary>
    private const int AddressLineMinLength = 5;

    /// <summary>
    /// Any run of the same character three or more times in a row ("aaa",
    /// "....", "------") - the shape of keyboard-mashing or a placeholder,
    /// never a real address line.
    /// </summary>
    private static readonly Regex RepeatedCharacterRun = new(@"(.)\1{2,}", RegexOptions.Compiled);

    public UpsertAddressRequestValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Line1)
            .NotEmpty()
            .MaximumLength(300)
            .Must(BeMeaningfulAddressLine)
            .WithMessage($"Address line 1 must be at least {AddressLineMinLength} characters of real address content, not filler text.");
        RuleFor(x => x.Line2)
            .MaximumLength(300)
            .Must(line => string.IsNullOrWhiteSpace(line) || BeMeaningfulAddressLine(line))
            .WithMessage($"Address line 2 must be at least {AddressLineMinLength} characters of real address content, not filler text.");
        RuleFor(x => x.Landmark).MaximumLength(200);
        // Task 334: was NotEmpty().MaximumLength(12), while customer-web's
        // AddressForm has always enforced ^\d{6}$ - so the two ends disagreed
        // about what a pincode is, and anything the frontend rejected could
        // still reach the API through any other client. Tightened rather than
        // relaxed because six digits is the real Indian format, and because
        // ProfileValidators already enforces exactly this rule with exactly
        // this message - AddressValidators was the lone outlier, not the
        // standard.
        RuleFor(x => x.Pincode)
            .NotEmpty()
            .Matches(@"^\d{6}$").WithMessage("Pincode must be 6 digits");
        RuleFor(x => x.City).NotEmpty().MaximumLength(100);
        RuleFor(x => x.State).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Latitude).InclusiveBetween(-90m, 90m);
        RuleFor(x => x.Longitude).InclusiveBetween(-180m, 180m);
        RuleFor(x => x.ContactName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ContactMobile)
            .NotEmpty()
            .Matches(@"^\+?[1-9]\d{7,14}$").WithMessage("Contact mobile must be a valid phone number");
    }

    /// <summary>
    /// Structural, not semantic, screen for an address line: long enough to
    /// plausibly be an address, contains at least some letters/digits (not
    /// only punctuation/whitespace), and isn't a mashed/placeholder run like
    /// "....." or "aaaaaa". Deliberately does not - and cannot - detect real
    /// but meaningless text (filler words, a wrong-language phrase): that
    /// needs address verification/geocoding, out of scope here (see the
    /// OPEN-FIXES-FEATURES.csv "Address snapshot" row).
    /// </summary>
    private static bool BeMeaningfulAddressLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string trimmed = line.Trim();
        if (trimmed.Length < AddressLineMinLength)
        {
            return false;
        }

        if (!trimmed.Any(char.IsLetterOrDigit))
        {
            return false;
        }

        return !RepeatedCharacterRun.IsMatch(trimmed);
    }
}
