using System.Globalization;

namespace ReconAI.Web.Services;

/// <summary>Culture-independent display formatting, so a screen looks the same on every machine.</summary>
public static class Fmt
{
    public static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    public static string Money(decimal? value) => value is { } v ? Money(v) : "";

    public static string Utc(DateTime value) => value.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";

    public static string Bytes(long value) => value < 1024 ? $"{value} B" : value < 1_048_576 ? $"{value / 1024.0:0.#} KB" : $"{value / 1_048_576.0:0.#} MB";
}
