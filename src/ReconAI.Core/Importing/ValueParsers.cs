using System.Globalization;
using System.Text.RegularExpressions;

namespace ReconAI.Core.Importing;

public readonly record struct ParseError(string Code, string Message);

/// <summary>
/// Strict value parsing for imported cells. Money is parsed straight into <see cref="decimal"/>: a value with more than
/// two decimal places is rejected, never rounded.
/// </summary>
public static class ValueParsers
{
    // 1234.50 | 1,234.50 | 12,34,567.00 (Indian grouping). Anything else, including 12,34 or 1e3, is not an amount.
    private static readonly Regex MoneyPattern = new(
        @"^(?<int>\d+|\d{1,3}(?:,\d{3})+|\d{1,2}(?:,\d{2})*,\d{3})(?<frac>\.\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CurrencyPrefix = new(@"^(?:₹|INR|Rs\.?)\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public const decimal MaxAmount = 999_999_999_999.99m;

    public static bool TryParseMoney(string? raw, out decimal value, out ParseError error)
    {
        value = 0;
        error = default;
        var s = (raw ?? "").Trim().Replace(' ', ' ');
        if (s.Length == 0)
        {
            error = new ParseError("AMOUNT_EMPTY", "Amount is empty");
            return false;
        }

        s = CurrencyPrefix.Replace(s, "");
        if (s.StartsWith('-') || s.StartsWith('(') || s.EndsWith(')'))
        {
            error = new ParseError("AMOUNT_NEGATIVE", $"Amount '{Shorten(raw)}' is negative; negative amounts and credit/debit markers are not supported");
            return false;
        }

        var match = MoneyPattern.Match(s);
        if (!match.Success)
        {
            error = new ParseError("AMOUNT_INVALID", $"'{Shorten(raw)}' is not a valid amount");
            return false;
        }

        var digits = match.Groups["int"].Value.Replace(",", "") + match.Groups["frac"].Value;
        if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
        {
            error = new ParseError("AMOUNT_INVALID", $"'{Shorten(raw)}' is not a valid amount");
            return false;
        }

        if (parsed != Math.Round(parsed, 2))
        {
            error = new ParseError("AMOUNT_DECIMALS", $"Amount '{Shorten(raw)}' has more than 2 decimal places (values are never rounded)");
            return false;
        }

        if (parsed > MaxAmount)
        {
            error = new ParseError("AMOUNT_TOO_LARGE", $"Amount '{Shorten(raw)}' is larger than the supported maximum");
            return false;
        }

        value = Math.Round(parsed, 2) + 0.00m;   // normalise to scale 2
        return true;
    }

    // Slash and dash dates are read day-first (Indian convention): 03/04/2026 is 3 April 2026.
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss",
        "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "d/M/yyyy HH:mm:ss",
        "dd-MM-yyyy", "d-M-yyyy",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MMM-yyyy HH:mm:ss", "dd MMM yyyy", "d MMM yyyy",
    ];

    public static bool TryParseDate(string? raw, out DateOnly value, out ParseError error)
    {
        value = default;
        error = default;
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
        {
            error = new ParseError("DATE_EMPTY", "Date is empty");
            return false;
        }

        if (!DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            error = new ParseError("DATE_INVALID", $"'{Shorten(raw)}' is not a recognised date (use yyyy-MM-dd, dd/MM/yyyy or dd-MMM-yyyy)");
            return false;
        }

        if (dt.Year is < 2000 or > 2100)
        {
            error = new ParseError("DATE_RANGE", $"Date '{Shorten(raw)}' is outside 2000-2100");
            return false;
        }

        value = DateOnly.FromDateTime(dt);
        return true;
    }

    /// <summary>Shortens a raw cell for display in an error message.</summary>
    public static string Shorten(string? raw, int max = 40)
    {
        var s = (raw ?? "").Trim();
        return s.Length <= max ? s : s[..max] + "...";
    }
}
