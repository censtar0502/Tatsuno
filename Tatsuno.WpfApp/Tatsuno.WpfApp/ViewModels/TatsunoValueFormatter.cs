using System.Globalization;

namespace Tatsuno.WpfApp.ViewModels;

internal static class TatsunoValueFormatter
{
    private static readonly CultureInfo Ru = new("ru-RU");

    public static string FormatVolume(int raw)
    {
        return (raw / 100.0m).ToString("N2", Ru);
    }

    public static string FormatMoney(int raw)
    {
        decimal displayed = raw * 10m;
        return displayed.ToString("N0", Ru);
    }

    public static int ParseDisplayedMoneyToRaw(string? text)
    {
        string digits = OnlyDigits(text);
        if (string.IsNullOrEmpty(digits))
        {
            return 0;
        }

        if (!int.TryParse(digits, out int displayed))
        {
            return 0;
        }

        // Protocol price field is 4 digits max (0000-9999).
        // Display = raw * 10  →  raw = display / 10.
        // Tatsuno protocol minimum price step = 10 display units (1 raw unit).
        // Prices not divisible by 10 (e.g. 1122) are rounded to nearest 10 (→ 1120).
        // Use AwayFromZero rounding (1125 → 1130, not 1120).
        int raw = (int)Math.Round(displayed / 10.0, MidpointRounding.AwayFromZero);
        return Math.Clamp(raw, 0, 9999);
    }

    /// <summary>
    /// Clamp a displayed price to the nearest value representable in the protocol
    /// (must be a multiple of 10, max 99990). Returns the clamped display value.
    /// </summary>
    public static int ClampDisplayedPrice(int displayedPrice)
    {
        int raw = displayedPrice / 10;
        raw = Math.Clamp(raw, 0, 9999);
        return raw * 10;
    }

    public static int ParseDisplayedVolumeToRaw(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        string normalized = text.Trim().Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
        {
            return 0;
        }

        value = decimal.Round(value * 100m, 0, MidpointRounding.AwayFromZero);
        if (value <= 0m)
        {
            return 0;
        }

        return (int)value;
    }

    public static string OnlyDigits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        char[] chars = text.Where(char.IsDigit).ToArray();
        return new string(chars);
    }
}
