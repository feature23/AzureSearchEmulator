using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Storage and encoding for <c>Collection(Edm.Single)</c> vector fields (issue #46).
/// </summary>
/// <remarks>
/// <para>
/// A vector is written twice, and which copy is authoritative matters.
/// </para>
/// <para>
/// The stored JSON sidecar is the source of truth. It is what retrieval reads, and — the
/// reason it cannot merely be an optimization — it is the only copy that survives a
/// <c>merge</c> action: <c>UpsertIndexDocumentActionBase.MergeDocument</c> rebuilds a document
/// from <c>IndexSearcher.Doc</c>, which returns stored fields and nothing else, so a vector
/// held only in doc values would be silently destroyed by a merge that touched an unrelated
/// field. Vector fields are also commonly declared hidden, so the sidecar is written whether or
/// not the field is retrievable, and <c>$select</c> decides what comes back.
/// </para>
/// <para>
/// The packed doc values copy is derived from it, and exists so that a query can read a
/// document's vector without materializing and re-parsing a 1536-element JSON array per
/// document per query. It is written as little-endian float32, which is both compact — an
/// index of vectors is within a fraction of a percent of the size of the raw floats — and
/// decodable straight into the span a similarity function wants.
/// </para>
/// <para>
/// Neither copy is an indexed term. Emitting one Lucene term per element, which is what the
/// ordinary collection path does, would put 1536 terms in the dictionary for a single
/// embedding and make the index unusable for the thing it is meant to demonstrate.
/// </para>
/// </remarks>
public static class VectorSearchSupport
{
    /// <summary>
    /// The element type that makes a collection a vector field.
    /// </summary>
    public const string VectorElementType = "Edm.Single";

    /// <summary>
    /// The field type a vector field declares.
    /// </summary>
    public const string VectorFieldType = "Collection(Edm.Single)";

    /// <summary>
    /// The smallest vector the emulator accepts.
    /// </summary>
    /// <remarks>
    /// Two, not one, matching the <c>minimum</c> the Azure REST specification puts on a field's
    /// <c>dimensions</c>. A one-dimensional vector is a scalar with extra steps — every metric
    /// degenerates on it — so the service has never allowed one.
    /// </remarks>
    public const int MinDimensions = 2;

    /// <summary>
    /// The largest vector the emulator accepts.
    /// </summary>
    /// <remarks>
    /// The <c>maximum</c> the Azure REST specification puts on <c>dimensions</c>. Matching both
    /// bounds keeps a definition the service would reject from being accepted here, which is the
    /// direction of error an emulator should prefer: a test that passes locally and fails
    /// against the service is worse than the reverse.
    /// </remarks>
    public const int MaxDimensions = 4096;

    /// <summary>
    /// Name of the doc values field holding a document's vector, packed as little-endian
    /// float32.
    /// </summary>
    /// <remarks>
    /// Prefixed like the emulator's other sidecars so it cannot collide with a user field name.
    /// </remarks>
    public static string GetVectorDocValuesFieldName(string path) => "__azs_vector__" + path;

    /// <summary>
    /// True when this field is a vector field rather than an ordinary collection.
    /// </summary>
    /// <remarks>
    /// The type alone is the test. A <c>Collection(Edm.Single)</c> without
    /// <see cref="SearchField.Dimensions"/> is not a usable vector field, but it is also not
    /// something else — it is a vector field with a missing declaration, and
    /// <see cref="Indexing.VectorSearchValidator"/> reports it as such rather than letting it
    /// fall through to a path that would index each float as its own term.
    /// </remarks>
    public static bool IsVectorField(this SearchField field)
        => string.Equals(field.Type, VectorFieldType, StringComparison.Ordinal);

    /// <summary>
    /// Reads a JSON array into a float vector, checking it against the field's declared
    /// dimensions.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The value is not an array of numbers, or its length does not match
    /// <paramref name="dimensions"/>. The indexing path turns this into a per-document 400,
    /// which is how Azure reports the same mistake.
    /// </exception>
    public static float[] ParseVector(string path, JsonNode value, int? dimensions)
    {
        if (value is not JsonArray array)
        {
            throw new InvalidOperationException(
                $"Field '{path}' is type {VectorFieldType} but received a non-array JSON value.");
        }

        if (dimensions is { } declared && array.Count != declared)
        {
            throw new InvalidOperationException(
                $"Field '{path}' expects a vector of {declared} dimensions but received " +
                $"{array.Count}.");
        }

        var vector = new float[array.Count];

        for (var i = 0; i < array.Count; i++)
        {
            // A null inside a vector is not the ignorable gap it is in an ordinary collection:
            // dropping it would silently shorten the vector and shift every later element into
            // the wrong dimension.
            if (array[i] is not JsonValue element)
            {
                throw new InvalidOperationException(
                    $"Field '{path}' has a null or non-numeric value at index {i}; every " +
                    "element of a vector must be a number.");
            }

            if (!TryReadSingle(element, out vector[i]))
            {
                throw new InvalidOperationException(
                    $"Field '{path}' has a non-numeric value at index {i}; every element of a " +
                    "vector must be a number.");
            }
        }

        return vector;
    }

    /// <summary>
    /// Reads one element of a vector as a float, whatever CLR type the JSON value happens to
    /// hold it in.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonValue.TryGetValue{T}"/> is stricter than it looks. A value parsed from
    /// text is backed by a <see cref="JsonElement"/> and converts to any numeric type, but one
    /// constructed in memory holds a specific CLR type and returns false for every other — so
    /// <c>TryGetValue&lt;float&gt;</c> fails on a <c>JsonValue</c> built from an <c>int</c>,
    /// even though the number is perfectly representable. Documents reach the emulator both
    /// ways: over HTTP they are parsed, and in tests they are often constructed. Checking
    /// <see cref="JsonNode.GetValueKind"/> first identifies a number in either representation,
    /// and the ordered attempts then cover the CLR types a JSON number can be held in.
    /// </remarks>
    private static bool TryReadSingle(JsonValue element, out float value)
    {
        value = 0;

        if (element.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        if (element.TryGetValue<float>(out var single))
        {
            value = single;
            return true;
        }

        if (element.TryGetValue<double>(out var doubleValue))
        {
            value = (float)doubleValue;
            return true;
        }

        if (element.TryGetValue<int>(out var integer))
        {
            value = integer;
            return true;
        }

        if (element.TryGetValue<long>(out var int64))
        {
            value = int64;
            return true;
        }

        if (element.TryGetValue<decimal>(out var dec))
        {
            value = (float)dec;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Packs a vector into the little-endian float32 layout the doc values field stores.
    /// </summary>
    public static byte[] PackVector(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];

        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    /// <summary>
    /// Unpacks a vector from the doc values representation into <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// Takes the destination as a parameter so a scan can reuse one buffer across every
    /// document rather than allocating an array per hit.
    /// </remarks>
    public static void UnpackVector(ReadOnlySpan<byte> packed, Span<float> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = BinaryPrimitives.ReadSingleLittleEndian(packed[(i * sizeof(float))..]);
        }
    }

    /// <summary>
    /// Creates the Lucene fields for one document's vector.
    /// </summary>
    /// <remarks>
    /// The stored copy holds the JSON the document supplied rather than a re-serialization of
    /// the parsed floats, so retrieval returns exactly what was uploaded. Re-emitting the
    /// parsed values would round <c>0.1</c> to <c>0.100000001490116</c> and make a document
    /// appear to change when it had not.
    /// </remarks>
    public static IEnumerable<IIndexableField> CreateFields(string path, JsonArray value, float[] vector)
    {
        yield return new StoredField(
            Indexing.SearchFieldExtensions.GetCollectionStorageFieldName(path),
            value.ToJsonString());

        yield return new BinaryDocValuesField(
            GetVectorDocValuesFieldName(path),
            new BytesRef(PackVector(vector)));
    }
}
