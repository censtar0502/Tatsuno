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

        return displayed / 10;
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
