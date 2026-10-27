using System.Globalization;

namespace AzureSearchEmulator.Dashboard;

/// <summary>
/// Formats a byte count for the index pages' storage sizes (issue #91).
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Bytes in the largest binary unit that keeps the number at or above 1, with one decimal
    /// place below 10 and none above, as the Azure portal shows them.
    /// </summary>
    public static string Format(long bytes)
    {
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 || value >= 10 ? "0" : "0.#";

        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
