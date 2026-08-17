using System.Text;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Util;

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
            return CreateStringFields(field, str, stored, path)
                .Concat(CreateFacetFields(field, field.Type, value, path));
        }

        if (field.Type == GeoSupport.GeographyPointType)
        {
            // A geography point needs more than one Lucene field, so it can't go through
            // CreateScalarField's single-field contract.
            var (lon, lat) = GeoSupport.ParseGeoJsonPoint(path, value);
            return GeoSupport.CreateFields(path, lon, lat, field.Retrievable);
        }

        return new[] { CreateScalarField(field.Type, value, stored, path) }
            .Concat(CreateFacetFields(field, field.Type, value, path));
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

                // Every element's leaves share the same Lucene field names. That makes them a
                // cheap candidate filter, but it does not preserve which values belonged to
                // the same element, so the elements are also written whole (below) for the
                // lambda filter to correlate against.
                fields.AddRange(CreateComplexObjectFields(field, element, path));
            }

            // Written regardless of retrievability: this is what filters read, and a hidden
            // complex collection still has to be filterable.
            fields.Add(new BinaryDocValuesField(
                ComplexTypeSupport.GetComplexElementsDocValuesFieldName(path),
                new BytesRef(Encoding.UTF8.GetBytes(array.ToJsonString()))));
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
            // their own whole-value copies. The outermost complex field already stored the
            // object, so a nested JSON sidecar would be dead weight retrieval never reads.
            //
            // The element doc values matter more: Lucene allows only one doc-values value per
            // field name per document, so a complex collection nested inside another would
            // overwrite itself once per outer element and throw at commit. The nested
            // elements are reachable inside the outer field's own copy, which is how the
            // lambda evaluator recurses into them.
            foreach (var indexField in CreateFields(subField, subValue, subPath))
            {
                if (subField.IsComplex()
                    && (indexField.Name == ComplexTypeSupport.GetComplexStorageFieldName(subPath)
                        || indexField.Name == ComplexTypeSupport.GetComplexElementsDocValuesFieldName(subPath)))
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
                fields.AddRange(CreateFacetFields(field, elementType, element, path));
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
                fields.AddRange(CreateFacetFields(field, elementType, element, path));
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

        if (filterable)
        {
            yield return new StringField(GetRawStringFieldName(path), str, Field.Store.NO);
        }
    }

    /// <summary>
    /// Name of the sidecar field holding a string's exact value, with no analysis applied.
    /// </summary>
    /// <remarks>
    /// A searchable field's analyzed and raw copies share the field's own name, which is
    /// enough for equality — a <c>TermQuery</c> asks for one exact term — but not for a range,
    /// which scans every term between its bounds and cannot tell an analyzed token from a raw
    /// value. "Alpha" lowercases to the analyzed term "alpha", which sorts after "Charlie" in
    /// Lucene's byte-wise term ordering, so <c>Name ge 'Charlie'</c> would match it.
    ///
    /// Writing the raw value once more under a name the analyzer never touches gives string
    /// ranges a field where every term is a real field value. It costs one extra term per
    /// value, and only for filterable string fields.
    /// </remarks>
    public static string GetRawStringFieldName(string path) => "__azs_raw__" + path;

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

    /// <summary>
    /// Builds the facet doc values for one value of a facetable field, converting the JSON
    /// value to the CLR value <see cref="FacetSupport"/> encodes.
    /// </summary>
    /// <remarks>
    /// This runs alongside the indexed and stored copies rather than replacing any of them:
    /// see <see cref="FacetSupport"/> for why facet counting needs values of its own.
    /// </remarks>
    private static IEnumerable<IIndexableField> CreateFacetFields(
        SearchField field,
        string type,
        JsonNode value,
        string path)
    {
        if (!field.Facetable.GetValueOrDefault())
        {
            return [];
        }

        object? facetValue = type switch
        {
            "Edm.String" => value.GetValue<string>(),
            "Edm.Int32" => value.GetValue<int>(),
            "Edm.Int64" => value.GetValue<long>(),
            "Edm.Double" => value.GetValue<double>(),
            "Edm.Boolean" => value.GetValue<bool>(),
            "Edm.DateTimeOffset" => value.GetValue<DateTimeOffset>(),
            // Geography and complex fields are not facetable; FacetRequest rejects a request
            // to facet on one, so nothing needs to be written here.
            _ => null,
        };

        return facetValue is null
            ? []
            : FacetSupport.CreateFacetFields(field, type, path, facetValue);
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
