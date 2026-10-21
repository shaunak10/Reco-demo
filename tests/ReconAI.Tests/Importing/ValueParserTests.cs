using ReconAI.Core.Importing;

namespace ReconAI.Tests.Importing;

public class ValueParserTests
{
    [Theory]
    [InlineData("599", "599.00")]
    [InlineData("599.00", "599.00")]
    [InlineData("599.5", "599.50")]
    [InlineData("1,234.50", "1234.50")]
    [InlineData("12,34,567.00", "1234567.00")]          // Indian digit grouping
    [InlineData("₹ 2,999.00", "2999.00")]
    [InlineData("Rs. 149", "149.00")]
    [InlineData("INR 9,757.19", "9757.19")]
    [InlineData("0.01", "0.01")]
    [InlineData("599.000", "599.00")]                   // trailing zeros are not extra precision
    [InlineData("  4983.20  ", "4983.20")]
    public void Valid_amounts_parse_exactly(string raw, string expected)
    {
        Assert.True(ValueParsers.TryParseMoney(raw, out var value, out _));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("", "AMOUNT_EMPTY")]
    [InlineData("   ", "AMOUNT_EMPTY")]
    [InlineData("abc", "AMOUNT_INVALID")]
    [InlineData("12,34", "AMOUNT_INVALID")]             // wrong thousands grouping must not become 1234
    [InlineData("1e3", "AMOUNT_INVALID")]
    [InlineData("599.", "AMOUNT_INVALID")]
    [InlineData("5 99", "AMOUNT_INVALID")]
    [InlineData("599.005", "AMOUNT_DECIMALS")]           // never rounded
    [InlineData("0.001", "AMOUNT_DECIMALS")]
    [InlineData("-599.00", "AMOUNT_NEGATIVE")]
    [InlineData("(599.00)", "AMOUNT_NEGATIVE")]
    [InlineData("1000000000000.00", "AMOUNT_TOO_LARGE")]
    public void Invalid_amounts_are_rejected_not_repaired(string raw, string code)
    {
        Assert.False(ValueParsers.TryParseMoney(raw, out _, out var error));
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("2026-10-15", 2026, 10, 15)]
    [InlineData("2026-10-15 14:22:10", 2026, 10, 15)]
    [InlineData("15/10/2026", 2026, 10, 15)]
    [InlineData("15/10/2026 14:22:10", 2026, 10, 15)]
    [InlineData("03/04/2026", 2026, 4, 3)]              // day-first: 3 April, not 4 March
    [InlineData("3/4/2026", 2026, 4, 3)]
    [InlineData("16-Oct-2026", 2026, 10, 16)]
    [InlineData("6-Oct-2026", 2026, 10, 6)]
    [InlineData("16 Oct 2026", 2026, 10, 16)]
    public void Dates_parse_day_first(string raw, int y, int m, int d)
    {
        Assert.True(ValueParsers.TryParseDate(raw, out var value, out _));
        Assert.Equal(new DateOnly(y, m, d), value);
    }

    [Theory]
    [InlineData("", "DATE_EMPTY")]
    [InlineData("31/02/2026", "DATE_INVALID")]
    [InlineData("13/13/2026", "DATE_INVALID")]
    [InlineData("tomorrow", "DATE_INVALID")]
    [InlineData("15/10/26", "DATE_INVALID")]            // two-digit years are ambiguous
    [InlineData("01/01/1999", "DATE_RANGE")]
    public void Invalid_dates_are_rejected(string raw, string code)
    {
        Assert.False(ValueParsers.TryParseDate(raw, out _, out var error));
        Assert.Equal(code, error.Code);
    }
}
