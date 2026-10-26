using System.Text.Json;
using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Semantic;

/// <summary>
/// One of an index's semantic configurations: the fields the ranker reads, in priority order.
/// </summary>
/// <remarks>
/// Read from the index's <c>semantic</c> property, which the emulator keeps verbatim among the
/// definition's unmodelled properties (issue #41) rather than as a typed model. Parsing it only
/// when a semantic query needs it leaves the stored definition exactly as the caller wrote it.
/// </remarks>
public sealed record SemanticConfiguration(
    string Name,
    string? TitleField,
    IReadOnlyList<string> ContentFields,
    IReadOnlyList<string> KeywordsFields)
{
    /// <summary>
    /// Finds the configuration a query names, or the index's default when it names none.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The index has no semantic configurations, has none of that name, or has no default for a
    /// query that does not name one — all faults in the request, and reported as 400.
    /// </exception>
    public static SemanticConfiguration Resolve(SearchIndex index, string? name)
    {
        if (index.AdditionalProperties?.TryGetValue("semantic", out var semantic) != true
            || semantic.ValueKind != JsonValueKind.Object
            || !semantic.TryGetProperty("configurations", out var configurations)
            || configurations.ValueKind != JsonValueKind.Array
            || configurations.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"The index '{index.Name}' has no semantic configurations, which a query of type 'semantic' requires.");
        }

        if (string.IsNullOrEmpty(name))
        {
            name = semantic.TryGetProperty("defaultConfiguration", out var defaultName)
                   && defaultName.ValueKind == JsonValueKind.String
                ? defaultName.GetString()
                : null;

            if (string.IsNullOrEmpty(name))
            {
                throw new InvalidOperationException(
                    $"The index '{index.Name}' has no default semantic configuration, so 'semanticConfiguration' must be specified.");
            }
        }

        foreach (var configuration in configurations.EnumerateArray())
        {
            if (configuration.TryGetProperty("name", out var configurationName)
                && configurationName.ValueKind == JsonValueKind.String
                && string.Equals(configurationName.GetString(), name, StringComparison.Ordinal))
            {
                return Parse(name, configuration);
            }
        }

        throw new InvalidOperationException(
            $"The semantic configuration '{name}' does not exist in the index '{index.Name}'.");
    }

    private static SemanticConfiguration Parse(string name, JsonElement configuration)
    {
        if (!configuration.TryGetProperty("prioritizedFields", out var fields) || fields.ValueKind != JsonValueKind.Object)
        {
            return new SemanticConfiguration(name, null, [], []);
        }

        var title = fields.TryGetProperty("titleField", out var titleField) ? GetFieldName(titleField) : null;

        return new SemanticConfiguration(
            name,
            title,
            GetFieldNames(fields, "prioritizedContentFields"),
            GetFieldNames(fields, "prioritizedKeywordsFields"));
    }

    private static List<string> GetFieldNames(JsonElement fields, string property)
        => fields.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(GetFieldName).OfType<string>().ToList()
            : [];

    private static string? GetFieldName(JsonElement field)
        => field.ValueKind == JsonValueKind.Object
           && field.TryGetProperty("fieldName", out var fieldName)
           && fieldName.ValueKind == JsonValueKind.String
            ? fieldName.GetString()
            : null;
}
