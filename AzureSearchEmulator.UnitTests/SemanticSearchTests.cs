using System.Text.Json;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using AzureSearchEmulator.Semantic;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// End-to-end tests for semantic queries answered by reranking retrieved documents (issue #109).
/// </summary>
/// <remarks>
/// Run with <see cref="LexicalSemanticScorer"/>, the default, or a scorer stubbed per test so the
/// reranking itself can be told apart from the retrieval order it replaces. The cross-encoder is
/// not exercised here: it needs a downloaded model.
/// </remarks>
public class SemanticSearchTests : IDisposable
{
    private readonly RAMDirectory _directory = new();
    private readonly SearchIndex _index = CreateIndex();
    private readonly SharedDirectoryFactory _factory;
    private readonly LuceneNetIndexWriterFactory _writerFactory;

    public SemanticSearchTests()
    {
        _factory = new SharedDirectoryFactory(_directory);
        _writerFactory = new LuceneNetIndexWriterFactory(_factory);

        new LuceneNetSearchIndexer(_writerFactory, _factory).IndexDocuments(_index,
        [
            new UploadIndexDocumentAction(Doc("history", "A history of the computer",
                "The computer of the 1940s was slow. A slow computer filled a room.", "history")),
            new UploadIndexDocumentAction(Doc("speedup", "Make an old laptop feel new",
                "Disable startup programs. An SSD is the biggest upgrade for a sluggish computer.", "performance")),
            new UploadIndexDocumentAction(Doc("bread", "Fix a dense loaf",
                "A slow rise makes a dense loaf. Let the dough proof somewhere warm.", "baking")),
        ]);
    }

    public void Dispose()
    {
        _writerFactory.Dispose();
        _directory.Dispose();
    }

    private static SearchIndex CreateIndex() => new()
    {
        Name = "semantic",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
            new SearchField { Name = "Title", Type = "Edm.String", Searchable = true },
            new SearchField { Name = "Content", Type = "Edm.String", Searchable = true },
            new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Searchable = true },
        ],
        AdditionalProperties = new Dictionary<string, JsonElement>
        {
            ["semantic"] = JsonSerializer.SerializeToElement(new
            {
                defaultConfiguration = "default",
                configurations = new[]
                {
                    new
                    {
                        name = "default",
                        prioritizedFields = new
                        {
                            titleField = new { fieldName = "Title" },
                            prioritizedContentFields = new[] { new { fieldName = "Content" } },
                            prioritizedKeywordsFields = new[] { new { fieldName = "Tags" } },
                        },
                    },
                },
            }),
        },
    };

    private static JsonObject Doc(string id, string title, string content, string tag) => new()
    {
        ["Id"] = id,
        ["Title"] = title,
        ["Content"] = content,
        ["Tags"] = new JsonArray(tag),
    };

    private LuceneNetIndexSearcher CreateSearcher(ISemanticScorer? scorer = null) => new(_factory, semanticScorer: scorer);

    private static List<string?> Ids(SearchResponse response)
        => response.Results.Select(r => r["Id"]?.GetValue<string>()).ToList();

    [Fact]
    public async Task SemanticQuery_ReordersByScorer_AndReportsRerankerScore()
    {
        // Prefers "speedup", which retrieval ranks last: only reranking can put it first.
        var scorer = new StubScorer(text => text.Contains("SSD") ? 0.9 : 0.2);

        var response = await CreateSearcher(scorer).Search(_index, new SearchRequest
        {
            Search = "slow computer",
            QueryType = "semantic",
        });

        Assert.Equal("speedup", Ids(response)[0]);
        Assert.Equal(3.6, response.Results[0]["@search.rerankerScore"]!.GetValue<double>(), 6);
        Assert.All(response.Results, r => Assert.NotNull(r["@search.score"]));
    }

    [Fact]
    public async Task SemanticQuery_WithLexicalScorer_ReturnsScoresInAzureRange()
    {
        var response = await CreateSearcher().Search(_index, new SearchRequest
        {
            Search = "slow computer",
            QueryType = "semantic",
        });

        Assert.NotEmpty(response.Results);
        Assert.All(response.Results, r =>
        {
            var score = r["@search.rerankerScore"]!.GetValue<double>();
            Assert.InRange(score, 0, 4);
        });

        var scores = response.Results.Select(r => r["@search.rerankerScore"]!.GetValue<double>()).ToList();
        Assert.Equal(scores.OrderByDescending(s => s), scores);
    }

    [Fact]
    public async Task SemanticQuery_PagesAfterReranking()
    {
        var scorer = new StubScorer(text => text.Contains("SSD") ? 0.9 : text.Contains("loaf") ? 0.5 : 0.1);

        var response = await CreateSearcher(scorer).Search(_index, new SearchRequest
        {
            Search = "slow computer",
            QueryType = "semantic",
            Skip = 1,
            Top = 1,
        });

        // Page two of the reranked order, not of the retrieval order.
        Assert.Equal(["bread"], Ids(response));
    }

    [Fact]
    public async Task Captions_AreTheBestPassage_WithQueryTermsHighlighted()
    {
        var response = await CreateSearcher().Search(_index, new SearchRequest
        {
            Search = "sluggish computer",
            QueryType = "semantic",
            Captions = "extractive",
            Select = "Id",
        });

        var speedup = response.Results.Single(r => r["Id"]!.GetValue<string>() == "speedup");
        var caption = speedup["@search.captions"]!.AsArray().Single()!;

        Assert.Equal("An SSD is the biggest upgrade for a sluggish computer.", caption["text"]!.GetValue<string>());
        Assert.Equal("An SSD is the biggest upgrade for a <em>sluggish</em> <em>computer</em>.",
            caption["highlights"]!.GetValue<string>());

        // Read from the configuration's fields even though $select left them out.
        Assert.Null(speedup["Content"]);
    }

    [Fact]
    public async Task Captions_WithHighlightOff_OmitHighlights()
    {
        var response = await CreateSearcher().Search(_index, new SearchRequest
        {
            Search = "computer",
            QueryType = "semantic",
            Captions = "extractive|highlight-false",
        });

        Assert.All(response.Results, r => Assert.Null(r["@search.captions"]![0]!["highlights"]));
    }

    [Fact]
    public async Task Answers_HonourCountAndThreshold()
    {
        var scorer = new StubScorer(text => text.Contains("SSD") ? 0.95 : text.Contains("loaf") ? 0.8 : 0.1);

        var response = await CreateSearcher(scorer).Search(_index, new SearchRequest
        {
            Search = "slow computer",
            QueryType = "semantic",
            Answers = "extractive|count-5,threshold-0.75",
        });

        Assert.NotNull(response.Answers);
        Assert.Equal(["speedup", "bread"], response.Answers.Select(a => a.Key));
        Assert.Equal(0.95, response.Answers[0].Score, 6);
    }

    [Fact]
    public async Task Answers_WhenNothingClearsThreshold_IsEmptyNotNull()
    {
        var response = await CreateSearcher(new StubScorer(_ => 0.1)).Search(_index, new SearchRequest
        {
            Search = "slow computer",
            QueryType = "semantic",
            Answers = "extractive",
        });

        Assert.NotNull(response.Answers);
        Assert.Empty(response.Answers);
    }

    [Fact]
    public async Task SemanticQuery_RanksAgainstSemanticQueryWhenGiven()
    {
        var scorer = new StubScorer((query, _) => query == "baking bread" ? 0.5 : 0);

        var response = await CreateSearcher(scorer).Search(_index, new SearchRequest
        {
            Search = "slow",
            SemanticQuery = "baking bread",
            QueryType = "semantic",
        });

        Assert.All(response.Results, r => Assert.Equal(2, r["@search.rerankerScore"]!.GetValue<double>(), 6));
    }

    [Fact]
    public async Task PlainQuery_HasNoSemanticOutput()
    {
        var response = await CreateSearcher().Search(_index, new SearchRequest { Search = "computer", Captions = "extractive" });

        Assert.Null(response.Answers);
        Assert.All(response.Results, r =>
        {
            Assert.Null(r["@search.rerankerScore"]);
            Assert.Null(r["@search.captions"]);
        });
    }

    [Theory]
    [InlineData("missing", null, null)]
    [InlineData(null, "abstractive", null)]
    [InlineData(null, "extractive|count-11", null)]
    [InlineData(null, "extractive|threshold-2", null)]
    [InlineData(null, null, "extractive|highlight-maybe")]
    public async Task InvalidSemanticRequest_IsRejected(string? configuration, string? answers, string? captions)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSearcher().Search(_index, new SearchRequest
        {
            Search = "computer",
            QueryType = "semantic",
            SemanticConfiguration = configuration,
            Answers = answers,
            Captions = captions,
        }));
    }

    [Fact]
    public async Task SemanticQuery_OnIndexWithoutSemanticConfiguration_IsRejected()
    {
        var index = CreateIndex();
        var bare = new SearchIndex { Name = index.Name, Fields = index.Fields };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSearcher().Search(bare, new SearchRequest
        {
            Search = "computer",
            QueryType = "semantic",
        }));

        Assert.Contains("no semantic configurations", ex.Message);
    }

    [Fact]
    public void LexicalScorer_PrefersPassagesCoveringMoreQueryWords()
    {
        var scores = new LexicalSemanticScorer().Score("the slow computer",
            ["a slow computer", "slow slow slow", "nothing relevant"]);

        Assert.True(scores[0] > scores[1]);
        Assert.True(scores[1] > scores[2]);
        Assert.Equal(0, scores[2]);
    }

    private sealed class StubScorer(Func<string, string, double> score) : ISemanticScorer
    {
        public StubScorer(Func<string, double> score) : this((_, passage) => score(passage))
        {
        }

        public IReadOnlyList<double> Score(string query, IReadOnlyList<string> passages)
            => passages.Select(p => score(query, p)).ToList();
    }

    private class SharedDirectoryFactory(Lucene.Net.Store.Directory directory) : ILuceneDirectoryFactory, ILuceneIndexReaderFactory
    {
        public Lucene.Net.Store.Directory GetDirectory(string indexName) => directory;
        public void ClearCachedDirectory(string indexName) { }
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}
