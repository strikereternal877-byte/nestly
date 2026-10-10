using FluentAssertions;
using Nestly.Infrastructure.Observability;

namespace Nestly.Catalog.Tests;

/// <summary>
/// <see cref="LogSanitizer"/> keeps a caller-supplied value on one line of a plain-text log (CWE-117). The service-level
/// proof that the payment webhooks use it lives next to their other tests.
/// </summary>
public class LogSanitizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_in_gives_an_empty_string_out(string? input) =>
        LogSanitizer.ForLog(input).Should().BeEmpty();

    [Fact]
    public void An_ordinary_gateway_order_id_is_left_untouched() =>
        LogSanitizer.ForLog("order_20261002_ab12~3").Should().Be("order_20261002_ab12~3");

    [Theory]
    [InlineData("a\r\nb", "a__b")]
    [InlineData("a\nb", "a_b")]
    [InlineData("a\rb", "a_b")]
    [InlineData("a\tb\u0000c\u001bd", "a_b_c_d")]
    public void Line_breaks_and_control_characters_become_underscores(string input, string expected) =>
        LogSanitizer.ForLog(input).Should().Be(expected);

    [Fact]
    public void Unicode_line_and_paragraph_separators_are_replaced_too()
    {
        string input = "a" + (char)0x85 + "b" + (char)0x2028 + "c" + (char)0x2029 + "d";

        LogSanitizer.ForLog(input).Should().Be("a_b_c_d");
    }

    [Fact]
    public void A_forged_second_log_entry_cannot_survive()
    {
        string forged = "order_1\r\n2026-10-02 12:00:00 [Information] Wallet top-up credited";

        LogSanitizer.ForLog(forged).Should().NotContainAny("\r", "\n");
    }

    [Fact]
    public void A_very_long_value_is_cut_so_it_cannot_flood_the_log()
    {
        string result = LogSanitizer.ForLog(new string('x', 5_000));

        result.Should().HaveLength(100);
    }
}
