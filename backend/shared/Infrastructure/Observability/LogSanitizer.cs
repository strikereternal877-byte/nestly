namespace Nestly.Infrastructure.Observability;

/// <summary>
/// Makes a caller-supplied string safe to write into a log line (CWE-117, log forging). Structured logging keeps a
/// value out of the message template, but sinks that write plain text (console, files) still print a raw value as-is,
/// so a webhook field containing a line break could forge a second, genuine-looking entry. Used for values that
/// arrive over the wire and are logged before - or independently of - any check against our own records, such as the
/// gateway order id on a payment webhook.
/// </summary>
public static class LogSanitizer
{
    /// <summary>Longer than any gateway order id we issue; the webhook validator already caps it at 100.</summary>
    private const int MaxLoggedLength = 100;

    private const char ReplacementCharacter = '_';

    // U+2028 / U+2029, written as numbers so the source file never contains a raw line-separator character.
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>
    /// Returns <paramref name="value"/> on a single line: line breaks, Unicode line/paragraph separators and every
    /// other control character become <c>_</c> and the result is cut to <see cref="MaxLoggedLength"/> characters.
    /// Never null.
    /// </summary>
    public static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string oneLine = value
            .Replace("\r", ReplacementCharacter.ToString(), StringComparison.Ordinal)
            .Replace("\n", ReplacementCharacter.ToString(), StringComparison.Ordinal);

        string bounded = oneLine.Length > MaxLoggedLength ? oneLine[..MaxLoggedLength] : oneLine;

        return string.Create(bounded.Length, bounded, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = IsUnsafeInLog(source[i]) ? ReplacementCharacter : source[i];
            }
        });
    }

    // Control characters, plus the Unicode line and paragraph separators, which some sinks also treat as a line break.
    private static bool IsUnsafeInLog(char character) =>
        char.IsControl(character) || character == LineSeparator || character == ParagraphSeparator;
}
