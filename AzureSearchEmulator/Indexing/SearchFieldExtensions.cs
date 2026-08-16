using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;
using Lucene.Net.Documents;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public static class SearchFieldExtensions
{
    public static IEnumerable<IIndexableField> CreateFields(this SearchField field, JsonNode value)
        => CreateFields(field, value, field.Name);

    /// <summary>
    /// Creates the Lucene fields for <paramref name="value"/>, indexing them under
    /// <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// The path differs from the field's own name only inside a complex type, where a
    /// sub-field is indexed under its full slash-delimited path (i.e. <c>Address/City</c>)
    /// so that filters written against that path resolve to a real Lucene field.
    /// </remarks>
    private static IEnumerable<IIndexableField> CreateFields(SearchField field, JsonNode value, string path)
    {
        if (field.IsComplex())
        {
            return CreateComplexFields(field, value, path);
        }

        if (field.Type.StartsWith("Collection(", StringComparison.Ordinal))
        {
            return CreateCollectionFields(field, value, path);
        }

        return CreateSingleValueFields(field, value, path);
    }

    private static IEnumerable<IIndexableField> CreateSingleValueFields(SearchField field, JsonNode value, string path)
    {
        var stored = field.Retrievable ? Field.Store.YES : Field.Store.NO;

        if (field.Type == "Edm.String")
        {
            var str = value.GetValue<string>();
            return CreateStringFields(field, str, stored, path);
        }

        if (field.Type == GeoSupport.GeographyPointType)
        {
            // A geography point needs more than one Lucene field, so it can't go through
            // CreateScalarField's single-field contract.
            var (lon, lat) = GeoSupport.ParseGeoJsonPoint(path, value);
            return GeoSupport.CreateFields(path, lon, lat, field.Retrievable);
        }

        return [CreateScalarField(field.Type, value, stored, path)];
    }

    /// <summary>
    /// Flattens a complex value (or a collection of them) into one Lucene field per leaf,
    /// plus a stored JSON sidecar used to reconstruct the original object on retrieval.
    /// </summary>
    private static IEnumerable<IIndexableField> CreateComplexFields(SearchField field, JsonNode value, string path)
    {
        var fields = new List<IIndexableField>();

        if (field.IsComplexCollection())
        {
            if (value is not JsonArray array)
            {
                throw new InvalidOperationException(
                    $"Field '{path}' is type {field.Type} but received a non-array JSON value.");
            }

            foreach (var element in array)
            {
                if (element is null)
                {
                    // Azure Search ignores null entries within a collection.
                    continue;
                }

                // Every element's leaves share the same Lucene field names, which is what
                // makes an any(...) lambda match a document when any one element qualifies.
                // The flip side is that leaves are no longer correlated per element — a
                // limitation Azure Search shares for filters that don't use a lambda.
                fields.AddRange(CreateComplexObjectFields(field, element, path));
            }
        }
        else
        {
            fields.AddRange(CreateComplexObjectFields(field, value, path));
        }

        if (field.Retrievable)
        {
            fields.Add(new StoredField(
                ComplexTypeSupport.GetComplexStorageFieldName(path),
                value.ToJsonString()));
        }

        return fields;
    }

    /// <summary>
    /// Indexes the sub-fields of a single complex object. Sub-fields absent from the JSON are
    /// skipped, and JSON properties with no matching sub-field are ignored, matching how
    /// Azure Search treats a document that does not populate every declared field.
    /// </summary>
    private static IEnumerable<IIndexableField> CreateComplexObjectFields(SearchField field, JsonNode value, string path)
    {
        if (value is not JsonObject obj)
        {
            throw new InvalidOperationException(
                $"Field '{path}' is type {field.Type} but received a non-object JSON value.");
        }

        foreach (var subField in field.Fields)
        {
            var subValue = obj.FirstOrDefault(p =>
                string.Equals(p.Key, subField.Name, StringComparison.OrdinalIgnoreCase)).Value;

            if (subValue is null)
            {
                continue;
            }

            var subPath = ComplexTypeSupport.CombinePath(path, subField.Name);

            // Nested complex sub-fields recurse through CreateFields, but must not each write
            // their own JSON sidecar: the outermost complex field already stored the whole
            // object, and a nested duplicate would be dead weight that retrieval never reads.
            foreach (var indexField in CreateFields(subField, subValue, subPath))
            {
                if (subField.IsComplex()
                    && indexField.Name == ComplexTypeSupport.GetComplexStorageFieldName(subPath))
                {
                    continue;
                }

                yield return indexField;
            }
        }
    }

    private static IEnumerable<IIndexableField> CreateCollectionFields(SearchField field, JsonNode value, string path)
    {
        if (value is not JsonArray array)
        {
            throw new InvalidOperationException(
                $"Field '{path}' is type {field.Type} but received a non-array JSON value.");
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
                fields.AddRange(CreateStringFields(field, element.GetValue<string>(), stored, path));
            }
            else if (elementType == GeoSupport.GeographyPointType)
            {
                // Every point goes under the same field names; the geo filters read them
                // back through doc values, which preserves all of a document's points.
                var (lon, lat) = GeoSupport.ParseGeoJsonPoint(path, element);
                fields.AddRange(GeoSupport.CreateFields(path, lon, lat, field.Retrievable));
            }
            else
            {
                fields.Add(CreateScalarField(elementType, element, stored, path));
            }
        }

        if (field.Retrievable)
        {
            fields.Add(new StoredField(GetCollectionStorageFieldName(path), array.ToJsonString()));
        }

        return fields;
    }

    private static IEnumerable<IIndexableField> CreateStringFields(SearchField field, string str, Field.Store stored, string path)
    {
        var searchable = field.Searchable.GetValueOrDefault(true);
        var filterable = field.Filterable;

        if (searchable)
        {
            yield return new TextField(path, str, stored);
            // Filter/sort/facet require a non-analyzed copy under the same field name
            // so TermQuery-based filters match the raw literal (matches Azure semantics).
            if (filterable || field.Sortable.GetValueOrDefault() || field.Facetable.GetValueOrDefault())
            {
                yield return new StringField(path, str, Field.Store.NO);
            }
        }
        else
        {
            yield return new StringField(path, str, stored);
        }
    }

    private static IIndexableField CreateScalarField(string type, JsonNode value, Field.Store stored, string path)
    {
        return type switch
        {
            "Edm.Int32" => new Int32Field(path, value.GetValue<int>(), stored),
            "Edm.Int64" => new Int64Field(path, value.GetValue<long>(), stored),
            "Edm.Double" => new DoubleField(path, value.GetValue<double>(), stored),
            "Edm.Boolean" => new Int32Field(path, value.GetValue<bool>() ? 1 : 0, stored),
            "Edm.DateTimeOffset" => new Int64Field(path, value.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds(), stored),
            // Edm.GeographyPoint is handled by the callers above, which can emit the
            // multiple Lucene fields a point requires. Edm.ComplexType is handled by
            // CreateComplexFields, which flattens it into its leaves.
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
