using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Lucene.Net.Documents;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public static class SearchFieldExtensions
{
    public static IEnumerable<IIndexableField> CreateFields(this SearchField field, JsonNode value)
    {
        if (field.Type.StartsWith("Collection(", StringComparison.Ordinal))
        {
            return CreateCollectionFields(field, value);
        }

        return CreateSingleValueFields(field, value);
    }

    private static IEnumerable<IIndexableField> CreateSingleValueFields(SearchField field, JsonNode value)
    {
        var stored = field.Retrievable ? Field.Store.YES : Field.Store.NO;

        if (field.Type == "Edm.String")
        {
            var str = value.GetValue<string>();
            return CreateStringFields(field, str, stored);
        }

        return [CreateScalarField(field, field.Type, value, stored)];
    }

    private static IEnumerable<IIndexableField> CreateCollectionFields(SearchField field, JsonNode value)
    {
        if (value is not JsonArray array)
        {
            throw new InvalidOperationException(
                $"Field '{field.Name}' is type {field.Type} but received a non-array JSON value.");
        }

        var elementType = GetCollectionElementType(field.Type);

        // Collections store each element as a separate Lucene field with the same name.
        // For retrievable collections we also persist the original JSON array under a
        // sidecar field so we can faithfully round-trip the values (preserving order
        // and type fidelity for numbers/booleans/dates) when reading the document back.
        var stored = Field.Store.NO;

        var fields = new List<IIndexableField>();

        foreach (var element in array)
        {
            if (element is null)
            {
                // Azure Search ignores null entries within a collection.
                continue;
            }

            if (elementType == "Edm.String")
            {
                fields.AddRange(CreateStringFields(field, element.GetValue<string>(), stored));
            }
            else
            {
                fields.Add(CreateScalarField(field, elementType, element, stored));
            }
        }

        if (field.Retrievable)
        {
            fields.Add(new StoredField(GetCollectionStorageFieldName(field.Name), array.ToJsonString()));
        }

        return fields;
    }

    private static IEnumerable<IIndexableField> CreateStringFields(SearchField field, string str, Field.Store stored)
    {
        var searchable = field.Searchable.GetValueOrDefault(true);
        var filterable = field.Filterable;

        if (searchable)
        {
            yield return new TextField(field.Name, str, stored);
            // Filter/sort/facet require a non-analyzed copy under the same field name
            // so TermQuery-based filters match the raw literal (matches Azure semantics).
            if (filterable || field.Sortable.GetValueOrDefault() || field.Facetable.GetValueOrDefault())
            {
                yield return new StringField(field.Name, str, Field.Store.NO);
            }
        }
        else
        {
            yield return new StringField(field.Name, str, stored);
        }
    }

    private static IIndexableField CreateScalarField(SearchField field, string type, JsonNode value, Field.Store stored)
    {
        return type switch
        {
            "Edm.Int32" => new Int32Field(field.Name, value.GetValue<int>(), stored),
            "Edm.Int64" => new Int64Field(field.Name, value.GetValue<long>(), stored),
            "Edm.Double" => new DoubleField(field.Name, value.GetValue<double>(), stored),
            "Edm.Boolean" => new Int32Field(field.Name, value.GetValue<bool>() ? 1 : 0, stored),
            "Edm.DateTimeOffset" => new Int64Field(field.Name, value.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds(), stored),
            "Edm.GeographyPoint" => throw new NotImplementedException(),
            "Edm.ComplexType" => throw new NotImplementedException(),
            _ => throw new InvalidOperationException($"Unsupported field type {type}")
        };
    }

    public static string GetCollectionElementType(string fieldType)
    {
        if (!fieldType.StartsWith("Collection(", StringComparison.Ordinal) || !fieldType.EndsWith(")", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{fieldType}' is not a collection type.");
        }

        return fieldType.Substring("Collection(".Length, fieldType.Length - "Collection(".Length - 1);
    }

    public static bool IsCollection(this SearchField field)
        => field.Type.StartsWith("Collection(", StringComparison.Ordinal);

    public static string GetCollectionStorageFieldName(string fieldName) => "__azs_collection__" + fieldName;
}
