using Azure.Search.Documents;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Semantic queries through the Azure SDK, which is what proves the response is shaped the way
/// the SDK reads it: reranker scores, captions and answers all land in typed properties (issue
/// #109).
/// </summary>
public class SemanticSearchIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    [Fact]
    public async Task SemanticQuery_PopulatesSdkSemanticResults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await SemanticFixture.CreateAsync(factory, cancellationToken);

        var response = await client.SearchAsync<SearchDocument>("capital city of France", SemanticFixture.Options(), cancellationToken);
        var results = await response.Value.GetResultsAsync().ToListAsync(cancellationToken);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.InRange(r.SemanticSearch.RerankerScore!.Value, 0, 4));
        Assert.Equal("paris", results[0].Document["id"]);
        Assert.Contains("<em>capital</em>", results[0].SemanticSearch.Captions.Single().Highlights);

        var answer = Assert.Single(response.Value.SemanticSearch.Answers);
        Assert.Equal("paris", answer.Key);
    }

    [Fact]
    public async Task SemanticQuery_WithUnknownConfiguration_IsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await SemanticFixture.CreateAsync(factory, cancellationToken);

        var options = SemanticFixture.Options();
        options.SemanticSearch.SemanticConfigurationName = "missing";

        var ex = await Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => client.SearchAsync<SearchDocument>("France", options, cancellationToken));

        Assert.Equal(400, ex.Status);
    }
}

/// <summary>
/// The same semantic query with the model-backed ranker enabled, which downloads the
/// cross-encoder into the container at startup.
/// </summary>
public class SemanticRankerModelIntegrationTests(SemanticRankerEmulatorFactory factory)
    : IClassFixture<SemanticRankerEmulatorFactory>
{
    [Fact]
    public async Task ModelRanker_PrefersMeaningOverSharedWords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // "trivia" says "capital" four times and is about money; "paris" never says it and is the
        // answer. Retrieval and the heuristic both put "trivia" first.
        var client = await SemanticFixture.CreateAsync(factory, cancellationToken,
        [
            new SearchDocument
            {
                ["id"] = "trivia",
                ["title"] = "What is the capital of a company?",
                ["content"] = "The capital of a company is the money its owners put in. Working capital is what is left for the day to day.",
            },
            new SearchDocument
            {
                ["id"] = "paris",
                ["title"] = "Paris",
                ["content"] = "Paris is the largest city in France and the seat of its government.",
            },
        ]);

        var response = await client.SearchAsync<SearchDocument>("what is the capital of France", SemanticFixture.Options(), cancellationToken);
        var results = await response.Value.GetResultsAsync().ToListAsync(cancellationToken);

        Assert.Equal(["paris", "trivia"], results.Select(r => (string)r.Document["id"]));
        Assert.True(results[0].Score < results[1].Score,
            "Retrieval should rank the answer second, or this test cannot tell reranking happened.");
        Assert.True(results[0].SemanticSearch.RerankerScore > 2);
    }
}

/// <summary>
/// An emulator container with the semantic ranker enabled.
/// </summary>
/// <remarks>
/// The model cache is a host directory that outlives the container, so only the first run on a
/// machine downloads the model.
/// </remarks>
public class SemanticRankerEmulatorFactory : EmulatorFactory
{
    private static readonly string ModelCache = Path.Combine(Path.GetTempPath(), "azsearchemu-tests-models");

    protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder)
    {
        Directory.CreateDirectory(ModelCache);

        // Docker Desktop accepts connections on a published port before anything listens behind
        // it, so the base factory's port check passes while the model is still downloading.
        // Kestrel only starts once the model is loaded, which makes its startup line the signal.
        return builder
            .WithEnvironment("Emulator__SemanticRanker__Enabled", "true")
            .WithBindMount(ModelCache, "/app/models", AccessMode.ReadWrite)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on"));
    }
}

internal static class SemanticFixture
{
    public static SearchOptions Options() => new()
    {
        QueryType = SearchQueryType.Semantic,
        SemanticSearch = new SemanticSearchOptions
        {
            QueryCaption = new QueryCaption(QueryCaptionType.Extractive),
            QueryAnswer = new QueryAnswer(QueryAnswerType.Extractive) { Threshold = 0.4 },
        },
    };

    public static async Task<SearchClient> CreateAsync(
        EmulatorFactory factory,
        CancellationToken cancellationToken,
        IEnumerable<SearchDocument>? documents = null)
    {
        var indexName = "semantic-" + Guid.NewGuid().ToString("N")[..8];

        await factory.CreateSearchIndexClient().CreateIndexAsync(
            new SearchIndex(indexName)
            {
                Fields =
                {
                    new SearchField("id", SearchFieldDataType.String) { IsKey = true },
                    new SearchField("title", SearchFieldDataType.String) { IsSearchable = true },
                    new SearchField("content", SearchFieldDataType.String) { IsSearchable = true },
                },
                SemanticSearch = new SemanticSearch
                {
                    DefaultConfigurationName = "default",
                    Configurations =
                    {
                        new SemanticConfiguration("default", new SemanticPrioritizedFields
                        {
                            TitleField = new SemanticField("title"),
                            ContentFields = { new SemanticField("content") },
                        }),
                    },
                },
            },
            cancellationToken);

        var client = factory.CreateSearchClient(indexName);

        await client.UploadDocumentsAsync(documents ??
        [
            new SearchDocument
            {
                ["id"] = "history",
                ["title"] = "A history of the slow computer",
                ["content"] = "The slow computer of the 1940s filled a room. Every computer then was slow.",
            },
            new SearchDocument
            {
                ["id"] = "speedup",
                ["title"] = "Make an old laptop feel new",
                ["content"] = "Disable startup programs. An SSD is the biggest upgrade for a sluggish computer.",
            },
            new SearchDocument
            {
                ["id"] = "paris",
                ["title"] = "France",
                ["content"] = "France is a country in Western Europe. Paris is its capital and largest city.",
            },
        ], cancellationToken: cancellationToken);

        return client;
    }
}
