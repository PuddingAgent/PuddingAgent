using System.Globalization;

namespace PuddingDesktop.Storage;

public static class StorageSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        var value = Math.Max(0, bytes);
        var amount = (double)value;
        var unitIndex = 0;
        while (amount >= 1024d && unitIndex < Units.Length - 1)
        {
            amount /= 1024d;
            unitIndex++;
        }

        var format = unitIndex == 0 ? "0" : amount >= 100 ? "0" : amount >= 10 ? "0.0" : "0.00";
        return $"{amount.ToString(format, CultureInfo.CurrentCulture)} {Units[unitIndex]}";
    }
}
