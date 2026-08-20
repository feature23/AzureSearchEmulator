using System.Text;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Indexing;

public static class SearchFieldExtensions
{
    public static IEnumerable<IIndexableField> CreateFields(
        this SearchField field,
        JsonNode value,
        SearchIndex? index = null)
        => CreateFields(field, value, field.Name, index);

    /// <summary>
    /// Creates the Lucene fields for <paramref name="value"/>, indexing them under
    /// <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// The path differs from the field's own name only inside a complex type, where a
    /// sub-field is indexed under its full slash-delimited path (i.e. <c>Address/City</c>)
    /// so that filters written against that path resolve to a real Lucene field.
    /// </remarks>
    private static IEnumerable<IIndexableField> CreateFields(
        SearchField field,
        JsonNode value,
        string path,
        SearchIndex? index)
    {
        if (field.IsComplex())
        {
            return CreateComplexFields(field, value, path, index);
        }

        if (field.IsVectorField())
        {
            // Checked before the general collection path: a vector is stored whole rather than
            // as one indexed term per element (issue #46).
            return CreateVectorFields(field, value, path);
        }

        if (field.Type.StartsWith("Collection(", StringComparison.Ordinal))
        {
            return CreateCollectionFields(field, value, path, index);
        }

        return CreateSingleValueFields(field, value, path, index);
    }

    private static IEnumerable<IIndexableField> CreateSingleValueFields(
        SearchField field,
        JsonNode value,
        string path,
        SearchIndex? index)
    {
        var stored = field.Retrievable ? Field.Store.YES : Field.Store.NO;

        if (field.Type == "Edm.String")
        {
            var str = value.GetValue<string>();
            return CreateStringFields(field, str, stored, path, index)
                .Concat(CreateFacetFields(field, field.Type, value, path, index));
        }

        if (field.Type == GeoSupport.GeographyPointType)
        {
            // A geography point needs more than one Lucene field, so it can't go through
            // CreateScalarField's single-field contract.
            var (lon, lat) = GeoSupport.ParseGeoJsonPoint(path, value);
            return GeoSupport.CreateFields(path, lon, lat, field.Retrievable);
        }

        return new[] { CreateScalarField(field.Type, value, stored, path) }
            .Concat(CreateFacetFields(field, field.Type, value, path, index));
    }

    /// <summary>
    /// Flattens a complex value (or a collection of them) into one Lucene field per leaf,
    /// plus a stored JSON sidecar used to reconstruct the original object on retrieval.
    /// </summary>
    private static IEnumerable<IIndexableField> CreateComplexFields(
        SearchField field,
        JsonNode value,
        string path,
        SearchIndex? index)
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
                fields.AddRange(CreateComplexObjectFields(field, element, path, index));
            }

            // Written regardless of retrievability: this is what filters read, and a hidden
            // complex collection still has to be filterable.
            fields.Add(new BinaryDocValuesField(
                ComplexTypeSupport.GetComplexElementsDocValuesFieldName(path),
                new BytesRef(Encoding.UTF8.GetBytes(array.ToJsonString()))));
        }
        else
        {
            fields.AddRange(CreateComplexObjectFields(field, value, path, index));
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
    private static IEnumerable<IIndexableField> CreateComplexObjectFields(
        SearchField field,
        JsonNode value,
        string path,
        SearchIndex? index)
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
            foreach (var indexField in CreateFields(subField, subValue, subPath, index))
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

    /// <summary>
    /// Creates the Lucene fields for a <c>Collection(Edm.Single)</c> vector field (issue #46).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CreateCollectionFields"/> because almost none of that path
    /// applies: a vector is not searched by term, filtered, sorted or faceted, so it wants
    /// neither the per-element indexed fields nor the facet doc values, and writing them would
    /// put one term per dimension into the dictionary. See <see cref="VectorSearchSupport"/>
    /// for what is written instead and why the stored copy is the authoritative one.
    /// </remarks>
    private static IEnumerable<IIndexableField> CreateVectorFields(SearchField field, JsonNode value, string path)
    {
        var vector = VectorSearchSupport.ParseVector(path, value, field.Dimensions);

        return VectorSearchSupport.CreateFields(path, (JsonArray)value, vector);
    }

    private static IEnumerable<IIndexableField> CreateCollectionFields(
        SearchField field,
        JsonNode value,
        string path,
        SearchIndex? index)
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
                fields.AddRange(CreateStringFields(field, element.GetValue<string>(), stored, path, index));
                fields.AddRange(CreateFacetFields(field, elementType, element, path, index));
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
                fields.AddRange(CreateFacetFields(field, elementType, element, path, index));
            }
        }

        if (field.Retrievable)
        {
            fields.Add(new StoredField(GetCollectionStorageFieldName(path), array.ToJsonString()));
        }

        return fields;
    }

    /// <summary>
    /// Writes the Lucene copies of one string value: the analyzed copy a search reads, and the
    /// exact-value copies a filter, sort or facet reads.
    /// </summary>
    /// <remarks>
    /// The exact-value copies are written through the field's normalizer and the analyzed copy
    /// is not (issue #74). The two serve different comparisons: a search matches tokens the
    /// field's analyzer produced, while a filter compares one whole value, and it is only the
    /// latter that a normalizer is defined to fold. Normalizing the analyzed copy as well would
    /// put the normalizer ahead of the analyzer in a chain Azure keeps separate.
    ///
    /// The stored copy is deliberately among the ones left alone: it is what a search result
    /// returns, and Azure returns the value as it was supplied, not as it was folded for
    /// comparison. Where the value is stored under the analyzed copy that falls out naturally;
    /// where the field is not searchable, the normalized copy is written separately so the
    /// stored one still carries the original.
    /// </remarks>
    private static IEnumerable<IIndexableField> CreateStringFields(
        SearchField field,
        string str,
        Field.Store stored,
        string path,
        SearchIndex? index)
    {
        var searchable = field.Searchable.GetValueOrDefault(true);
        var filterable = field.Filterable;
        var comparable = filterable || field.Sortable.GetValueOrDefault() || field.Facetable.GetValueOrDefault();

        var normalized = comparable ? NormalizerHelper.Normalize(index, field, str) : str;

        if (searchable)
        {
            yield return new TextField(path, str, stored);
            // Filter/sort/facet require a non-analyzed copy under the same field name
            // so TermQuery-based filters match the raw literal (matches Azure semantics).
            if (comparable)
            {
                yield return new StringField(path, normalized, Field.Store.NO);
            }
        }
        else if (normalized == str)
        {
            yield return new StringField(path, str, stored);
        }
        else
        {
            // The value the field is compared on and the value it returns have diverged, so
            // they need a field each: the indexed copy carries the normalized term, and the
            // stored copy keeps what the document supplied.
            yield return new StringField(path, normalized, Field.Store.NO);

            if (stored == Field.Store.YES)
            {
                yield return new StoredField(path, str);
            }
        }

        // Written for a sortable field as well as a filterable one. A sort over a searchable
        // field cannot read the field's own name: that name carries the analyzed tokens beside
        // the exact-value copy, and Lucene orders a document by the lowest term under the name,
        // so a multi-token value would sort by whichever of its words came first alphabetically.
        // The sidecar holds one term per document, which is what an ordering needs.
        if (filterable || field.IsSortable())
        {
            yield return new StringField(GetRawStringFieldName(path), normalized, Field.Store.NO);
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
        string path,
        SearchIndex? index)
    {
        if (!field.Facetable.GetValueOrDefault())
        {
            return [];
        }

        object? facetValue = type switch
        {
            // Normalized so that the values counted are the folded ones: without this a field
            // whose normalizer lowercases would still report "Las Vegas" and "LAS VEGAS" as two
            // distinct buckets, which is the case the feature exists to fix (issue #74).
            "Edm.String" => NormalizerHelper.Normalize(index, field, value.GetValue<string>()),
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

    /// <summary>
    /// Whether the field may be named in an <c>$orderby</c> expression.
    /// </summary>
    /// <remarks>
    /// An omitted <c>sortable</c> does not simply mean false. Azure documents the default as
    /// "true for single-valued simple fields, false for multi-valued simple fields, and null
    /// for complex fields", so a collection — whose several values give nothing to order by —
    /// is the only simple case that defaults off.
    /// </remarks>
    public static bool IsSortable(this SearchField field)
        => field.Sortable ?? (!field.IsCollection() && !field.IsComplex());

    public static string GetCollectionStorageFieldName(string fieldName) => "__azs_collection__" + fieldName;
}
