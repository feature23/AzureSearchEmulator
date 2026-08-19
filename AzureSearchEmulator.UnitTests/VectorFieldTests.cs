using System.Buffers.Binary;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests for the encoding of a vector field's values (issue #46).
/// </summary>
public class VectorEncodingTests
{
    [Fact]
    public void PackAndUnpack_RoundTripsExactly()
    {
        float[] vector = [0.1f, -2.5f, 0f, 3.4028235e38f, -1.4e-45f];

        var packed = VectorSearchSupport.PackVector(vector);
        var unpacked = new float[vector.Length];
        VectorSearchSupport.UnpackVector(packed, unpacked);

        Assert.Equal(vector, unpacked);
    }

    /// <summary>
    /// The packed layout is part of what is written to disk, so its size and byte order are
    /// worth pinning rather than leaving to inference.
    /// </summary>
    [Fact]
    public void PackVector_IsLittleEndianFloat32()
    {
        var packed = VectorSearchSupport.PackVector([1f, 2f]);

        Assert.Equal(8, packed.Length);
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(packed));
        Assert.Equal(2f, BinaryPrimitives.ReadSingleLittleEndian(packed.AsSpan(4)));
    }

    [Fact]
    public void ParseVector_ReadsNumbers()
    {
        var value = new JsonArray(1, 2.5, -3);

        var vector = VectorSearchSupport.ParseVector("embedding", value, 3);

        Assert.Equal([1f, 2.5f, -3f], vector);
    }

    /// <summary>
    /// The path a real upload takes. A document arriving over HTTP is parsed from text, so a
    /// whole number in a vector reaches the parser as an int and a fractional one as a double;
    /// neither is a float, and reading them as one has to work regardless.
    /// </summary>
    [Fact]
    public void ParseVector_ReadsNumbersParsedFromJsonText()
    {
        var value = JsonNode.Parse("[1, 2.5, -3, 4e2]")!;

        var vector = VectorSearchSupport.ParseVector("embedding", value, 4);

        Assert.Equal([1f, 2.5f, -3f, 400f], vector);
    }

    /// <summary>
    /// Azure rejects a document whose vector does not match the declared length, and so must
    /// the emulator: the mismatch is exactly the mistake a test against a real index would
    /// catch.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ParseVector_RejectsWrongLength(int declared)
    {
        var value = new JsonArray(1, 2, 3);

        var ex = Assert.Throws<InvalidOperationException>(
            () => VectorSearchSupport.ParseVector("embedding", value, declared));

        Assert.Contains(declared.ToString(), ex.Message);
        Assert.Contains("embedding", ex.Message);
    }

    [Fact]
    public void ParseVector_RejectsNonArray()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VectorSearchSupport.ParseVector("embedding", JsonValue.Create(1), 1));

        Assert.Contains("embedding", ex.Message);
    }

    /// <summary>
    /// A null inside a vector is not the ignorable gap it is in an ordinary collection: dropping
    /// it would shorten the vector and shift every later element into the wrong dimension.
    /// </summary>
    [Fact]
    public void ParseVector_RejectsNullElement()
    {
        var value = new JsonArray(1, null, 3);

        var ex = Assert.Throws<InvalidOperationException>(
            () => VectorSearchSupport.ParseVector("embedding", value, 3));

        Assert.Contains("index 1", ex.Message);
    }

    [Fact]
    public void ParseVector_RejectsNonNumericElement()
    {
        var value = new JsonArray(1, "two", 3);

        Assert.Throws<InvalidOperationException>(
            () => VectorSearchSupport.ParseVector("embedding", value, 3));
    }

    /// <summary>
    /// A vector field with no declared dimensions is refused at definition time, so parsing
    /// does not also have to insist on one.
    /// </summary>
    [Fact]
    public void ParseVector_WithoutDeclaredDimensions_AcceptsAnyLength()
    {
        var vector = VectorSearchSupport.ParseVector("embedding", new JsonArray(1, 2), null);

        Assert.Equal(2, vector.Length);
    }
}

/// <summary>
/// End-to-end tests for vector field storage, driving the real indexer so that what is written
/// to Lucene is what a document uploaded over HTTP would produce (issue #46).
/// </summary>
public class VectorFieldTests : IDisposable
{
    private readonly RAMDirectory _directory = new();
    private readonly SearchIndex _index;
    private readonly LuceneNetIndexWriterFactory _writerFactory;
    private readonly LuceneNetSearchIndexer _indexer;
    private readonly LuceneNetIndexSearcher _searcher;

    public VectorFieldTests()
    {
        _index = new SearchIndex
        {
            Name = "vectors",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
                new SearchField
                {
                    Name = "Embedding",
                    Type = "Collection(Edm.Single)",
                    Searchable = true,
                    Filterable = false,
                    Dimensions = 3,
                    VectorSearchProfile = "vp"
                },
            ],
            VectorSearch = new VectorSearch
            {
                Algorithms = [new VectorSearchAlgorithm { Name = "algo" }],
                Profiles = [new VectorSearchProfile { Name = "vp", Algorithm = "algo" }]
            }
        };

        var factory = new SharedDirectoryFactory(_directory);
        _writerFactory = new LuceneNetIndexWriterFactory(factory);
        _indexer = new LuceneNetSearchIndexer(_writerFactory, factory);
        _searcher = new LuceneNetIndexSearcher(factory);
    }

    public void Dispose()
    {
        _writerFactory.Dispose();
        _directory.Dispose();
    }

    private static JsonObject Doc(string id, string name, float[] embedding) => new()
    {
        ["Id"] = id,
        ["Name"] = name,
        ["Embedding"] = new JsonArray(embedding.Select(f => (JsonNode)f).ToArray()),
    };

    [Fact]
    public async Task Upload_ThenGetDoc_ReturnsVector()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "First", [0.5f, -1f, 2f]))]);

        var doc = await _searcher.GetDoc(_index, "1");

        var embedding = doc?["Embedding"] as JsonArray;
        Assert.NotNull(embedding);
        Assert.Equal([0.5f, -1f, 2f], embedding!.Select(n => n!.GetValue<float>()).ToArray());
    }

    [Fact]
    public void Upload_WithWrongDimensions_FailsThatDocument()
    {
        var result = _indexer.IndexDocuments(
            _index,
            [new UploadIndexDocumentAction(Doc("1", "First", [1f, 2f]))]);

        var status = Assert.Single(result.Value);
        Assert.False(status.Status);
        Assert.Contains("3", status.ErrorMessage);
    }

    /// <summary>
    /// One bad document must not take the rest of the batch with it, which is how Azure reports
    /// a partial failure.
    /// </summary>
    [Fact]
    public async Task Upload_WithOneBadVector_StillIndexesTheRest()
    {
        var result = _indexer.IndexDocuments(_index,
        [
            new UploadIndexDocumentAction(Doc("1", "Good", [1f, 2f, 3f])),
            new UploadIndexDocumentAction(Doc("2", "Bad", [1f])),
            new UploadIndexDocumentAction(Doc("3", "Also good", [4f, 5f, 6f])),
        ]);

        Assert.Collection(result.Value,
            r => Assert.True(r.Status),
            r => Assert.False(r.Status),
            r => Assert.True(r.Status));

        Assert.NotNull(await _searcher.GetDoc(_index, "1"));
        Assert.Null(await _searcher.GetDoc(_index, "2"));
        Assert.NotNull(await _searcher.GetDoc(_index, "3"));
    }

    /// <summary>
    /// The vector is stored as its original JSON rather than a re-serialization of the parsed
    /// floats, so a value that does not round-trip through float formatting comes back as it
    /// was written rather than appearing to have changed.
    /// </summary>
    [Fact]
    public async Task Upload_PreservesTheJsonAsWritten()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "First", [0.1f, 0.2f, 0.3f]))]);

        var doc = await _searcher.GetDoc(_index, "1");
        var embedding = (doc?["Embedding"] as JsonArray)!;

        Assert.Equal("0.1", embedding[0]!.ToJsonString());
    }

    /// <summary>
    /// This is the constraint that decides where a vector is stored. <c>MergeDocument</c>
    /// rebuilds a document from <c>IndexSearcher.Doc</c>, which returns stored fields and
    /// nothing else, so a vector held only in doc values would be silently destroyed by a merge
    /// that touched an unrelated field.
    /// </summary>
    [Fact]
    public async Task Merge_OfAnotherField_KeepsTheVector()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "First", [1f, 2f, 3f]))]);

        _indexer.IndexDocuments(_index, [new MergeIndexDocumentAction(new JsonObject
        {
            ["Id"] = "1",
            ["Name"] = "Renamed"
        })]);

        var doc = await _searcher.GetDoc(_index, "1");

        Assert.Equal("Renamed", doc?["Name"]?.GetValue<string>());
        var embedding = doc?["Embedding"] as JsonArray;
        Assert.NotNull(embedding);
        Assert.Equal([1f, 2f, 3f], embedding!.Select(n => n!.GetValue<float>()).ToArray());
    }

    /// <summary>
    /// The half of the merge behaviour that retrieval cannot show. The stored sidecar survives a
    /// merge on its own, but the packed doc-values copy — the one a vector query scans — does
    /// not, so a merged document would keep coming back correctly from <c>GetDoc</c> while
    /// silently disappearing from vector search.
    /// </summary>
    /// <remarks>
    /// Asserted against the doc values directly rather than through retrieval, because that is
    /// exactly the gap the original test left: it checked the sidecar, which was never the copy
    /// at risk.
    /// </remarks>
    [Fact]
    public void Merge_OfAnotherField_KeepsTheSearchableVectorCopy()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "First", [1f, 2f, 3f]))]);

        _indexer.IndexDocuments(_index, [new MergeIndexDocumentAction(new JsonObject
        {
            ["Id"] = "1",
            ["Name"] = "Renamed"
        })]);

        using var reader = DirectoryReader.Open(_directory);
        var atomic = reader.Leaves[0].AtomicReader;
        var docValues = atomic.GetBinaryDocValues(VectorSearchSupport.GetVectorDocValuesFieldName("Embedding"));

        Assert.NotNull(docValues);

        var bytes = new BytesRef();
        docValues.Get(0, bytes);

        var vector = new float[3];
        VectorSearchSupport.UnpackVector(bytes.Bytes.AsSpan(bytes.Offset, bytes.Length), vector);

        Assert.Equal([1f, 2f, 3f], vector);
    }

    /// <summary>
    /// A merge that supplies a new vector keeps the new one, rather than the rebuild restoring
    /// the old value over it.
    /// </summary>
    [Fact]
    public void Merge_ThatReplacesTheVector_KeepsTheNewDocValues()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "First", [1f, 2f, 3f]))]);

        _indexer.IndexDocuments(_index, [new MergeIndexDocumentAction(new JsonObject
        {
            ["Id"] = "1",
            ["Embedding"] = new JsonArray(7f, 8f, 9f)
        })]);

        using var reader = DirectoryReader.Open(_directory);
        var atomic = reader.Leaves[0].AtomicReader;
        var docValues = atomic.GetBinaryDocValues(VectorSearchSupport.GetVectorDocValuesFieldName("Embedding"));

        var bytes = new BytesRef();
        docValues.Get(0, bytes);

        var vector = new float[3];
        VectorSearchSupport.UnpackVector(bytes.Bytes.AsSpan(bytes.Offset, bytes.Length), vector);

        Assert.Equal([7f, 8f, 9f], vector);
    }

    [Fact]
    public async Task Merge_CanReplaceTheVector()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "First", [1f, 2f, 3f]))]);

        _indexer.IndexDocuments(_index, [new MergeIndexDocumentAction(new JsonObject
        {
            ["Id"] = "1",
            ["Embedding"] = new JsonArray(7f, 8f, 9f)
        })]);

        var embedding = (await _searcher.GetDoc(_index, "1"))?["Embedding"] as JsonArray;

        Assert.Equal([7f, 8f, 9f], embedding!.Select(n => n!.GetValue<float>()).ToArray());
    }

    /// <summary>
    /// A vector field is commonly declared hidden, and a hidden field must not come back in
    /// results even though its value is stored.
    /// </summary>
    [Fact]
    public async Task HiddenVectorField_IsNotReturned()
    {
        _index.Fields[2].Retrievable = false;

        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "First", [1f, 2f, 3f]))]);

        var doc = await _searcher.GetDoc(_index, "1");

        Assert.NotNull(doc);
        Assert.False(doc!.ContainsKey("Embedding"));
        Assert.Equal("First", doc["Name"]?.GetValue<string>());
    }

    /// <summary>
    /// Emitting one Lucene term per element — which is what the ordinary collection path does —
    /// would put a term per dimension into the dictionary and make the index unusable at any
    /// realistic embedding size.
    /// </summary>
    [Fact]
    public void VectorField_WritesNoIndexedTerms()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "First", [1f, 2f, 3f]))]);

        using var reader = DirectoryReader.Open(_directory);
        var terms = MultiFields.GetTerms(reader, "Embedding");

        Assert.Null(terms);
    }

    /// <summary>
    /// The packed copy is what a query will scan, so it has to be present and decodable.
    /// </summary>
    [Fact]
    public void VectorField_WritesPackedDocValues()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "First", [1f, 2f, 3f]))]);

        using var reader = DirectoryReader.Open(_directory);
        var atomic = reader.Leaves[0].AtomicReader;
        var docValues = atomic.GetBinaryDocValues(VectorSearchSupport.GetVectorDocValuesFieldName("Embedding"));

        Assert.NotNull(docValues);

        var bytes = new BytesRef();
        docValues.Get(0, bytes);

        var vector = new float[3];
        VectorSearchSupport.UnpackVector(bytes.Bytes.AsSpan(bytes.Offset, bytes.Length), vector);

        Assert.Equal([1f, 2f, 3f], vector);
    }

    /// <summary>
    /// A text vector query needs a hosted embedding model, so it stays unsupported even now
    /// that vector queries are answered.
    /// </summary>
    [Fact]
    public async Task VectorQuery_OfKindText_IsRefused()
    {
        var request = new SearchRequest
        {
            VectorQueries = [new VectorQuery { Kind = "text", Text = "a query", Fields = "Embedding" }]
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _searcher.Search(_index, request));

        Assert.Contains("embedding model", ex.Message);
    }

    /// <summary>
    /// vectorFilterMode only qualifies a vector query, so on its own it changes nothing and is
    /// not worth refusing a request over.
    /// </summary>
    [Fact]
    public async Task VectorFilterMode_Alone_DoesNotRefuseTheRequest()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "First", [1f, 2f, 3f]))]);

        var response = await _searcher.Search(_index, new SearchRequest
        {
            Search = "First",
            VectorFilterMode = "preFilter"
        });

        Assert.Single(response.Results);
    }

    /// <summary>
    /// Single-RAMDirectory backed factory pair shared between indexer and searcher so writes are
    /// visible to subsequent reads within the same test.
    /// </summary>
    private class SharedDirectoryFactory(Lucene.Net.Store.Directory directory)
        : ILuceneDirectoryFactory, ILuceneIndexReaderFactory
    {
        public Lucene.Net.Store.Directory GetDirectory(string indexName) => directory;
        public void ClearCachedDirectory(string indexName) { }
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}
