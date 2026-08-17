using System.Buffers.Binary;
using System.Globalization;
using AzureSearchEmulator.Models;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Indexing;

/// <summary>
/// The per-document values a facetable field contributes, written at index time and read back
/// when counting facet buckets.
/// </summary>
/// <remarks>
/// Facet counting cannot reuse any of the field copies already written for search, filtering,
/// or retrieval, because none of them can be read back faithfully for every facetable field:
///
/// <list type="bullet">
/// <item>The searchable copy is analyzed, so its terms are tokens
/// (<c>resort</c>, <c>spa</c>) rather than the value a bucket is named after
/// (<c>Resort and Spa</c>).</item>
/// <item>Numeric fields are indexed as trie terms, several per value, at
/// precisions that exist to make range queries fast and are meaningless as buckets.</item>
/// <item>Stored fields hold only <em>retrievable</em> values, but a field may be facetable
/// while hidden, and a collection's values are stored only as a retrievable JSON
/// sidecar.</item>
/// </list>
///
/// So facetable fields get their own doc values, written once per value in the field's own
/// terms: the exact string, or the number in the numeric space the facet ranges are compared
/// in. <see cref="SortedSetDocValuesField"/> is the multi-valued facility available in this
/// Lucene version, which is what lets a collection contribute each of its elements.
/// </remarks>
public static class FacetSupport
{
    /// <summary>
    /// The doc values field a facetable field's values are written under, kept separate from
    /// the field's own name so it cannot collide with the searchable or filterable copies.
    /// </summary>
    public static string GetFacetDocValuesFieldName(string path) => "__azs_facet__" + path;

    /// <summary>
    /// Builds the facet doc values for one value of a facetable field, or nothing when the
    /// field is not facetable.
    /// </summary>
    /// <remarks>
    /// <paramref name="type"/> is the element type for a collection, since each element is
    /// written separately and it is the element that gets counted.
    /// </remarks>
    public static IEnumerable<IIndexableField> CreateFacetFields(
        SearchField field,
        string type,
        string path,
        object value)
    {
        if (!field.Facetable.GetValueOrDefault())
        {
            yield break;
        }

        // Geography and complex values are not facetable in Azure Search, so nothing is
        // written for them; FacetRequest rejects such a request up front.
        var encoded = Encode(type, value);

        if (encoded is null)
        {
            yield break;
        }

        yield return new SortedSetDocValuesField(GetFacetDocValuesFieldName(path), encoded);
    }

    /// <summary>
    /// Encodes a value so that doc-values ordinals sort the way the facet's own values do,
    /// which is what lets <c>sort:value</c> and range bucketing read them back in order.
    /// </summary>
    /// <remarks>
    /// Strings are their own bytes. Numbers and dates are written as big-endian doubles with
    /// the sign bit flipped (and the remaining bits inverted when negative), the standard
    /// trick for making IEEE-754 doubles compare correctly as unsigned bytes — without it,
    /// negative values would sort after positive ones. Booleans become 0 and 1 so that
    /// <c>false</c> precedes <c>true</c>.
    /// </remarks>
    private static BytesRef? Encode(string type, object value) => type switch
    {
        "Edm.String" => new BytesRef((string)value),
        "Edm.Int32" => EncodeNumber((int)value),
        "Edm.Int64" => EncodeNumber((long)value),
        "Edm.Double" => EncodeNumber((double)value),
        "Edm.Boolean" => EncodeNumber((bool)value ? 1 : 0),
        "Edm.DateTimeOffset" => EncodeNumber(((DateTimeOffset)value).ToUnixTimeMilliseconds()),
        _ => null,
    };

    private static BytesRef EncodeNumber(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);

        // Flip the sign bit for positives; invert everything for negatives. This maps the
        // doubles onto unsigned integers that preserve numeric order.
        bits = bits < 0 ? ~bits : bits ^ long.MinValue;

        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, bits);

        return new BytesRef(bytes);
    }

    private static double DecodeNumber(ReadOnlySpan<byte> bytes)
    {
        var bits = BinaryPrimitives.ReadInt64BigEndian(bytes);

        bits = bits < 0 ? bits ^ long.MinValue : ~bits;

        return BitConverter.Int64BitsToDouble(bits);
    }

    /// <summary>
    /// Builds a per-document accessor yielding every facet value a document holds for a
    /// field, as the raw doc-values bytes.
    /// </summary>
    /// <remarks>
    /// Returns an empty list for a document with no value, which is how a null or empty field
    /// drops out of the facet counts rather than forming a bucket of its own.
    /// </remarks>
    public static Func<int, IReadOnlyList<BytesRef>> GetValueReader(AtomicReader reader, string path)
    {
        var values = reader.GetSortedSetDocValues(GetFacetDocValuesFieldName(path));

        if (values is null)
        {
            return _ => [];
        }

        return doc =>
        {
            values.SetDocument(doc);

            var result = new List<BytesRef>();

            for (var ord = values.NextOrd(); ord != SortedSetDocValues.NO_MORE_ORDS; ord = values.NextOrd())
            {
                var term = new BytesRef();
                values.LookupOrd(ord, term);

                // The ordinal's bytes are reused by the enumerator, so each value is copied
                // out before the next lookup overwrites it.
                result.Add(BytesRef.DeepCopyOf(term));
            }

            return result;
        };
    }

    /// <summary>
    /// Reads an encoded facet value back as a string, for a string-valued facet.
    /// </summary>
    public static string DecodeString(BytesRef bytes) => bytes.Utf8ToString();

    /// <summary>
    /// Reads an encoded facet value back as the number it was written from. Dates come back
    /// as Unix-epoch milliseconds, the space their range bounds are compared in.
    /// </summary>
    public static double DecodeNumber(BytesRef bytes) =>
        DecodeNumber(bytes.Bytes.AsSpan(bytes.Offset, bytes.Length));

    /// <summary>
    /// Renders a decoded facet value as the JSON-facing value for its field type.
    /// </summary>
    public static object? ToBucketValue(string type, BytesRef bytes) => type switch
    {
        "Edm.String" => DecodeString(bytes),
        "Edm.Int32" => (int)DecodeNumber(bytes),
        "Edm.Int64" => (long)DecodeNumber(bytes),
        "Edm.Double" => DecodeNumber(bytes),
        "Edm.Boolean" => DecodeNumber(bytes) != 0,
        "Edm.DateTimeOffset" => DateTimeOffset.FromUnixTimeMilliseconds((long)DecodeNumber(bytes))
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        _ => null,
    };
}
