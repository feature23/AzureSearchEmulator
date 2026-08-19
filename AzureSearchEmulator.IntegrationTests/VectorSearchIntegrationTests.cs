using System.Net;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;
using SearchIndex = Azure.Search.Documents.Indexes.Models.SearchIndex;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for vector search (issue #46), run against a containerized emulator
/// through the real Azure Search SDK.
/// </summary>
/// <remarks>
/// <para>
/// The SDK is the point of these tests. It models <c>VectorSearch</c>, the two algorithm
/// configurations, <c>VectorSearchProfile</c> and <c>VectorizedQuery</c> as strongly-typed
/// classes, and it decides the property names, the discriminators and the enum spellings that
/// go on the wire. A definition or a query the emulator read under a different name would fail
/// to round-trip here rather than pass unnoticed — which is what "API parity" has to mean in
/// practice, and something a unit test against the emulator's own models cannot establish.
/// </para>
/// <para>
/// Ranking assertions check which documents came back and in what order. That is the part the
/// emulator undertakes to reproduce for every metric; the absolute score is only faithful for
/// cosine, whose formula Azure documents, and the one test that asserts a literal score says so.
/// </para>
/// </remarks>
public class VectorSearchIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    /// <summary>
    /// Unit vectors along the three axes, plus one halfway between x and y. A query along x
    /// ranks them x, xy, then the two orthogonal axes — obvious by inspection rather than an
    /// artefact of the arithmetic.
    /// </summary>
    private static readonly float[] AlongX = [1f, 0f, 0f];

    /// <summary>
    /// Every property of a vector configuration survives a create-then-read cycle through the
    /// SDK, including the tuning parameters the emulator accepts and ignores.
    /// </summary>
    [Fact]
    public async Task VectorSearchConfiguration_RoundTripsThroughTheSdk()
    {
        const string indexName = "test-vector-round-trip";
        var indexClient = factory.CreateSearchIndexClient();

        await CreateIndexAsync(indexClient, indexName);

        var stored = await indexClient.GetIndexAsync(indexName, TestContext.Current.CancellationToken);

        var vectorSearch = stored.Value.VectorSearch;
        Assert.NotNull(vectorSearch);

        var hnsw = Assert.IsType<HnswAlgorithmConfiguration>(
            vectorSearch.Algorithms.Single(i => i.Name == "hnswAlgo"));
        Assert.Equal(VectorSearchAlgorithmMetric.Cosine, hnsw.Parameters?.Metric);

        // Accepted and ignored when answering a query, but dropping them would delete
        // configuration from the caller's own definition.
        Assert.Equal(4, hnsw.Parameters?.M);
        Assert.Equal(400, hnsw.Parameters?.EfConstruction);
        Assert.Equal(500, hnsw.Parameters?.EfSearch);

        var exhaustive = Assert.IsType<ExhaustiveKnnAlgorithmConfiguration>(
            vectorSearch.Algorithms.Single(i => i.Name == "exhaustiveAlgo"));
        Assert.Equal(VectorSearchAlgorithmMetric.DotProduct, exhaustive.Parameters?.Metric);

        var profile = vectorSearch.Profiles.Single(i => i.Name == "vp");
        Assert.Equal("hnswAlgo", profile.AlgorithmConfigurationName);

        var field = stored.Value.Fields.Single(i => i.Name == "Embedding");
        Assert.Equal(SearchFieldDataType.Collection(SearchFieldDataType.Single), field.Type);
        Assert.Equal(3, field.VectorSearchDimensions);
        Assert.Equal("vp", field.VectorSearchProfileName);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VectorQuery_RanksByProximity()
    {
        const string indexName = "test-vector-ranking";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var ids = await SearchIdsAsync(searchClient, VectorOptions(AlongX, k: 4));

        Assert.Equal("x", ids[0]);
        Assert.Equal("xy", ids[1]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The one metric whose absolute score Azure documents, as
    /// <c>1 / (1 + cosine_distance)</c>: an exact match scores 1 and an orthogonal vector 0.5.
    /// </summary>
    [Fact]
    public async Task CosineScore_MatchesAzuresDocumentedFormula()
    {
        const string indexName = "test-vector-cosine-score";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var response = await searchClient.SearchAsync<VectorDocument>(
            null, VectorOptions(AlongX, k: 4), TestContext.Current.CancellationToken);

        var results = await response.Value.GetResultsAsync()
            .ToListAsync(TestContext.Current.CancellationToken);

        var scores = results.ToDictionary(i => i.Document.Id, i => i.Score);

        Assert.Equal(1.0, scores["x"]!.Value, 4);
        Assert.Equal(0.5, scores["y"]!.Value, 4);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <c>k</c> bounds how many neighbours the query contributes, which the SDK sends as
    /// <c>kNearestNeighborsCount</c>.
    /// </summary>
    [Fact]
    public async Task KNearestNeighborsCount_BoundsTheResults()
    {
        const string indexName = "test-vector-k";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var ids = await SearchIdsAsync(searchClient, VectorOptions(AlongX, k: 2));

        Assert.Equal(["x", "xy"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// preFilter is the default and narrows the candidates before the neighbours are chosen, so
    /// a full <c>k</c> of matching documents comes back even though a nearer document was
    /// excluded.
    /// </summary>
    [Fact]
    public async Task PreFilter_NarrowsCandidatesBeforeSelection()
    {
        const string indexName = "test-vector-prefilter";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = VectorOptions(AlongX, k: 2);
        options.Filter = "Category eq 'axis'";
        options.VectorSearch!.FilterMode = VectorFilterMode.PreFilter;

        var ids = await SearchIdsAsync(searchClient, options);

        // xy is nearer than y but is filtered out, so the two nearest 'axis' documents come
        // back rather than one.
        Assert.Equal(2, ids.Count);
        Assert.Equal("x", ids[0]);
        Assert.DoesNotContain("xy", ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// postFilter applies after selection, so an excluded document still consumes one of the
    /// <c>k</c> slots and fewer than <c>k</c> results come back. This is the difference the mode
    /// exists to express.
    /// </summary>
    [Fact]
    public async Task PostFilter_AppliesAfterSelection()
    {
        const string indexName = "test-vector-postfilter";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = VectorOptions(AlongX, k: 2);
        options.Filter = "Category eq 'axis'";
        options.VectorSearch!.FilterMode = VectorFilterMode.PostFilter;

        var ids = await SearchIdsAsync(searchClient, options);

        // The two nearest are x and xy; xy is then filtered out, leaving one.
        Assert.Equal(["x"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A document with no embedding has no similarity to the query at all, so it is left out
    /// rather than ranked last.
    /// </summary>
    [Fact]
    public async Task DocumentWithoutAnEmbedding_IsNotReturned()
    {
        const string indexName = "test-vector-missing";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var ids = await SearchIdsAsync(searchClient, VectorOptions(AlongX, k: 100));

        Assert.DoesNotContain("novector", ids);
        Assert.Equal(4, ids.Count);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A vector field is usually hidden. Hiding it must not stop it being searched — only stop
    /// it coming back — and the emulator has to keep storing the value either way.
    /// </summary>
    [Fact]
    public async Task HiddenVectorField_IsSearchableButNotReturned()
    {
        const string indexName = "test-vector-hidden";
        var indexClient = factory.CreateSearchIndexClient();

        await CreateIndexAsync(indexClient, indexName, hideEmbedding: true);
        var searchClient = factory.CreateSearchClient(indexName);
        await UploadDocumentsAsync(searchClient);

        var response = await searchClient.SearchAsync<VectorDocument>(
            null, VectorOptions(AlongX, k: 1), TestContext.Current.CancellationToken);

        var result = await response.Value.GetResultsAsync()
            .FirstAsync(TestContext.Current.CancellationToken);

        Assert.Equal("x", result.Document.Id);
        Assert.Null(result.Document.Embedding);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// $top pages the neighbours k selected; the two are different knobs.
    /// </summary>
    [Fact]
    public async Task Size_PagesTheNeighbours()
    {
        const string indexName = "test-vector-paging";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = VectorOptions(AlongX, k: 4);
        options.Size = 2;

        var ids = await SearchIdsAsync(searchClient, options);

        Assert.Equal(["x", "xy"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Count_ReportsTheNumberOfNeighbours()
    {
        const string indexName = "test-vector-count";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = VectorOptions(AlongX, k: 3);
        options.IncludeTotalCount = true;

        var response = await searchClient.SearchAsync<VectorDocument>(
            null, options, TestContext.Current.CancellationToken);

        Assert.Equal(3, response.Value.TotalCount);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The metric comes from the field's profile, and dot product orders these two documents the
    /// opposite way to cosine — which is the observable proof that the profile is consulted
    /// rather than cosine assumed.
    /// </summary>
    [Fact]
    public async Task Metric_ComesFromTheFieldsProfile()
    {
        const string indexName = "test-vector-metric";
        var indexClient = factory.CreateSearchIndexClient();

        // Bind the field to the dotProduct profile rather than the cosine one.
        await CreateIndexAsync(indexClient, indexName, profileName: "exhaustive");
        var searchClient = factory.CreateSearchClient(indexName);

        var batch = IndexDocumentsBatch.Upload(new[]
        {
            // Points exactly at the query but is short; cosine prefers it.
            new VectorDocument { Id = "aligned", Title = "Aligned", Category = "a", Embedding = [0.1f, 0f, 0f] },
            // Points near the query and is long; dot product prefers it.
            new VectorDocument { Id = "long", Title = "Long", Category = "b", Embedding = [5f, 2f, 0f] },
        });

        await searchClient.IndexDocumentsAsync(batch, cancellationToken: TestContext.Current.CancellationToken);

        var ids = await SearchIdsAsync(searchClient, VectorOptions(AlongX, k: 1));

        Assert.Equal(["long"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A query vector of the wrong length is a fault in the request, and reporting it is far
    /// more useful than the empty result set the scan would otherwise produce — which looks
    /// identical to a genuine absence of near neighbours.
    /// </summary>
    [Fact]
    public async Task QueryVectorOfWrongLength_IsRejected()
    {
        const string indexName = "test-vector-bad-length";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => searchClient.SearchAsync<VectorDocument>(
                null,
                VectorOptions([1f, 0f], k: 3),
                TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.BadRequest, ex.Status);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A document whose embedding does not match the field's declared dimensions is rejected on
    /// its own, without failing the rest of the batch — which is how Azure reports a partial
    /// failure.
    /// </summary>
    [Fact]
    public async Task DocumentWithWrongDimensions_FailsOnlyThatDocument()
    {
        const string indexName = "test-vector-bad-document";
        var indexClient = factory.CreateSearchIndexClient();

        await CreateIndexAsync(indexClient, indexName);
        var searchClient = factory.CreateSearchClient(indexName);

        var batch = IndexDocumentsBatch.Upload(new[]
        {
            new VectorDocument { Id = "good", Title = "Good", Category = "a", Embedding = [1f, 0f, 0f] },
            new VectorDocument { Id = "bad", Title = "Bad", Category = "a", Embedding = [1f, 0f] },
        });

        var result = await searchClient.IndexDocumentsAsync(
            batch, cancellationToken: TestContext.Current.CancellationToken);

        // Per-document outcomes rather than a batch-level status: the point is that one bad
        // document does not take the others with it.
        var outcomes = result.Value.Results.ToDictionary(i => i.Key, i => i);

        Assert.True(outcomes["good"].Succeeded);
        Assert.False(outcomes["bad"].Succeeded);
        Assert.Equal((int)HttpStatusCode.BadRequest, outcomes["bad"].Status);
        Assert.Contains("3", outcomes["bad"].ErrorMessage);

        // And the good document really was indexed.
        var ids = await SearchIdsAsync(searchClient, VectorOptions(AlongX, k: 10));

        Assert.Equal(["good"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A hybrid query — text and vector together — is fused with Reciprocal Rank Fusion, so a
    /// document both arms rate well outranks one that a single arm rates best.
    /// </summary>
    /// <remarks>
    /// The documents are arranged so the arms disagree, because agreement proves nothing: any
    /// combination strategy ranks a document both arms love at the top. <c>vectoronly</c> is
    /// identical to the query vector but matches no text; <c>textonly</c> matches the text and
    /// points away from the vector; <c>both</c> matches the text and is second-nearest. Neither
    /// arm ranks <c>both</c> first.
    /// </remarks>
    [Fact]
    public async Task HybridSearch_FusesTheArmsWithRrf()
    {
        const string indexName = "test-vector-hybrid";
        var indexClient = factory.CreateSearchIndexClient();

        await CreateIndexAsync(indexClient, indexName);
        var searchClient = factory.CreateSearchClient(indexName);
        await UploadHybridDocumentsAsync(searchClient);

        var options = VectorOptions(AlongX, k: 2);

        var ids = await SearchIdsAsync(searchClient, options, search: "widget");

        Assert.Equal("both", ids[0]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The score in hybrid mode is the RRF score, which is small by construction — Azure warns
    /// that a value near 0.03 still indicates a strong match. A document ranked first by both
    /// arms scores exactly <c>float32(2/61)</c>, the value Azure's own documentation publishes.
    /// </summary>
    [Fact]
    public async Task HybridScore_IsTheRrfScore()
    {
        const string indexName = "test-vector-hybrid-score";
        var indexClient = factory.CreateSearchIndexClient();

        await CreateIndexAsync(indexClient, indexName);
        var searchClient = factory.CreateSearchClient(indexName);

        var batch = IndexDocumentsBatch.Upload(new[]
        {
            new VectorDocument { Id = "winner", Title = "widget", Category = "a", Embedding = [1f, 0f, 0f] },
        });

        await searchClient.IndexDocumentsAsync(batch, cancellationToken: TestContext.Current.CancellationToken);

        var response = await searchClient.SearchAsync<VectorDocument>(
            "widget", VectorOptions(AlongX, k: 1), TestContext.Current.CancellationToken);

        var result = await response.Value.GetResultsAsync().FirstAsync(TestContext.Current.CancellationToken);

        // float32(2/61). Asserted to 8 places rather than exactly: the SDK surfaces the score as
        // a double, and the JSON it is read from carries the single-precision value's shortest
        // round-trippable form, so the trailing digits of the float32 do not survive the wire.
        Assert.Equal(0.032786883413791656, result.Score!.Value, 8);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Weighting a vector query scales every term that arm contributes, which the SDK sends as
    /// <c>weight</c> on the query.
    /// </summary>
    [Fact]
    public async Task VectorQueryWeight_ScalesThatArmsContribution()
    {
        const string indexName = "test-vector-hybrid-weight";
        var indexClient = factory.CreateSearchIndexClient();

        await CreateIndexAsync(indexClient, indexName);
        var searchClient = factory.CreateSearchClient(indexName);
        await UploadHybridDocumentsAsync(searchClient);

        var weighted = VectorOptions(AlongX, k: 2);
        weighted.VectorSearch!.Queries[0].Weight = 10f;

        var response = await searchClient.SearchAsync<VectorDocument>(
            "widget", weighted, TestContext.Current.CancellationToken);

        var results = await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);
        var scores = results.ToDictionary(i => i.Document.Id, i => i.Score!.Value);

        // Returned by the vector arm alone, so its whole score scales with the weight.
        Assert.Equal(10.0 / 61.0, scores["vectoronly"], 5);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Documents whose arms disagree, so that fusion has something to demonstrate. The text
    /// scores tie deliberately: Lucene normalizes term frequency, so repeating a term does not
    /// reliably outrank a single occurrence.
    /// </summary>
    private static async Task UploadHybridDocumentsAsync(SearchClient searchClient)
    {
        var batch = IndexDocumentsBatch.Upload(new[]
        {
            new VectorDocument { Id = "textonly", Title = "widget", Category = "a", Embedding = [0f, 0f, 1f] },
            new VectorDocument { Id = "vectoronly", Title = "unrelated", Category = "a", Embedding = [1f, 0f, 0f] },
            new VectorDocument { Id = "both", Title = "widget", Category = "a", Embedding = [0.9f, 0.1f, 0f] },
            new VectorDocument { Id = "filler", Title = "widget", Category = "a", Embedding = [0f, -1f, 0f] },
        });

        await searchClient.IndexDocumentsAsync(batch, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static SearchOptions VectorOptions(float[] vector, int k)
    {
        var options = new SearchOptions
        {
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(vector)
                    {
                        KNearestNeighborsCount = k,
                        Fields = { "Embedding" },
                    },
                },
            },
        };

        return options;
    }

    private async Task<(SearchIndexClient, SearchClient)> SetUpAsync(string indexName)
    {
        var indexClient = factory.CreateSearchIndexClient();

        await CreateIndexAsync(indexClient, indexName);

        var searchClient = factory.CreateSearchClient(indexName);

        await UploadDocumentsAsync(searchClient);

        return (indexClient, searchClient);
    }

    private static async Task CreateIndexAsync(
        SearchIndexClient indexClient,
        string indexName,
        bool hideEmbedding = false,
        string profileName = "vp")
    {
        var index = new SearchIndex(indexName)
        {
            Fields =
            [
                new SimpleField("Id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SearchableField("Title"),
                new SimpleField("Category", SearchFieldDataType.String) { IsFilterable = true },
                new SearchField("Embedding", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    IsHidden = hideEmbedding,
                    VectorSearchDimensions = 3,
                    VectorSearchProfileName = profileName,
                },
            ],
            VectorSearch = new VectorSearch
            {
                Algorithms =
                {
                    new HnswAlgorithmConfiguration("hnswAlgo")
                    {
                        Parameters = new HnswParameters
                        {
                            M = 4,
                            EfConstruction = 400,
                            EfSearch = 500,
                            Metric = VectorSearchAlgorithmMetric.Cosine,
                        },
                    },
                    new ExhaustiveKnnAlgorithmConfiguration("exhaustiveAlgo")
                    {
                        Parameters = new ExhaustiveKnnParameters
                        {
                            Metric = VectorSearchAlgorithmMetric.DotProduct,
                        },
                    },
                },
                Profiles =
                {
                    new VectorSearchProfile("vp", "hnswAlgo"),
                    new VectorSearchProfile("exhaustive", "exhaustiveAlgo"),
                },
            },
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);
    }

    private static async Task UploadDocumentsAsync(SearchClient searchClient)
    {
        var batch = IndexDocumentsBatch.Upload(new[]
        {
            new VectorDocument { Id = "x", Title = "Along X", Category = "axis", Embedding = [1f, 0f, 0f] },
            new VectorDocument { Id = "y", Title = "Along Y", Category = "axis", Embedding = [0f, 1f, 0f] },
            new VectorDocument { Id = "z", Title = "Along Z", Category = "axis", Embedding = [0f, 0f, 1f] },
            new VectorDocument
            {
                Id = "xy", Title = "Between X and Y", Category = "diagonal",
                Embedding = [0.7071f, 0.7071f, 0f],
            },
            // No embedding at all, so the rule that such a document is left out has something
            // to act on.
            new VectorDocument { Id = "novector", Title = "No Vector", Category = "axis" },
        });

        await searchClient.IndexDocumentsAsync(batch, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> SearchIdsAsync(
        SearchClient searchClient,
        SearchOptions options,
        string? search = null)
    {
        var response = await searchClient.SearchAsync<VectorDocument>(
            search, options, TestContext.Current.CancellationToken);

        var results = await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);

        return results.Select(i => i.Document.Id).ToList();
    }
}
