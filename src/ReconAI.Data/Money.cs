using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ReconAI.Data;

/// <summary>
/// Money is <see cref="decimal"/> in C#. SQLite has no exact decimal type, so it is stored as an INTEGER number of paise:
/// exact, and SUM() in SQL is exact too. Converting a value that has more than two decimal places throws instead of
/// rounding, so nothing can be silently rounded at the persistence boundary.
///
/// On SQL Server, remove this converter and map the same properties as <c>decimal(18,2)</c>.
/// </summary>
public static class Money
{
    public static long ToPaise(decimal value)
    {
        var paise = value * 100m;
        if (paise != decimal.Truncate(paise))
            throw new InvalidOperationException($"Refusing to store {value}: more than two decimal places would be rounded.");
        return decimal.ToInt64(paise);
    }

    public static decimal FromPaise(long paise) => paise / 100m + 0.00m;   // scale 2, e.g. 599.00

    public static readonly ValueConverter<decimal, long> Converter = new(v => ToPaise(v), p => FromPaise(p));
}
