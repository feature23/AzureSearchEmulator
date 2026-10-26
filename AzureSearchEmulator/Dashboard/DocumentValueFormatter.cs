using System.Collections;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AzureSearchEmulator.Dashboard;

/// <summary>
/// Turns a field value from an SDK search result into the text the search page shows for it
/// (issue #92).
/// </summary>
public static class DocumentValueFormatter
{
    /// <summary>
    /// Collections of numbers longer than this are summarised instead of listed. Anything that long
    /// is a vector in practice, and a few hundred dimensions of floats tell a reader nothing beyond
    /// how many there are.
    /// </summary>
    public const int MaxListedNumbers = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Format(object? value) => value switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f when IsNumber(value) => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable list and not IDictionary when IsLongNumberList(list, out var count) => $"[{count} numbers]",
        _ => ToJson(value)
    };

    private static bool IsLongNumberList(IEnumerable list, out int count)
    {
        count = 0;

        foreach (var item in list)
        {
            if (!IsNumber(item))
            {
                return false;
            }

            count++;
        }

        return count > MaxListedNumbers;
    }

    private static bool IsNumber(object? value) =>
        value is int or long or double or float or decimal or short or byte or sbyte;

    private static string ToJson(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            // Geography values and the like deserialize to SDK types System.Text.Json has no
            // converter for; their ToString is at least recognisable.
            return value.ToString() ?? "";
        }
    }
}
