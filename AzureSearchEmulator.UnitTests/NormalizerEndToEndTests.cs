using System.Text.Json;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Exercises normalizers through the real indexing and search path (issue #74).
/// </summary>
/// <remarks>
/// This is the part of the feature that can only be tested here. A normalizer matches nothing
/// unless the identical transformation reaches the value written into the index and the literal
/// the filter compares against it; running the chain in isolation proves neither. Every test
/// below is written as the scenario Azure's documentation gives — variants of "Las Vegas" that
/// a filter, a facet or a sort should treat as one value.
/// </remarks>
public class NormalizerEndToEndTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private const string IndexJson =
        """
        {
          "name": "cities",
          "fields": [
            { "name": "id", "type": "Edm.String", "key": true },
            {
              "name": "city", "type": "Edm.String", "searchable": true,
              "filterable": true, "facetable": true, "sortable": true,
              "normalizer": "lowercase"
            },
            {
              "name": "code", "type": "Edm.String", "searchable": false,
              "filterable": true, "normalizer": "standard"
            },
            {
              "name": "tags", "type": "Collection(Edm.String)", "searchable": false,
              "filterable": true, "facetable": true, "normalizer": "lowercase"
            },
            { "name": "plain", "type": "Edm.String", "filterable": true }
          ]
        }
        """;

    private static SearchIndex CreateIndex() => JsonSerializer.Deserialize<SearchIndex>(IndexJson, Options)!;

    /// <summary>
    /// Builds documents through the real indexing path, so the tests read the same Lucene
    /// fields an uploaded document would produce.
    /// </summary>
    private static List<Document> CreateDocuments(SearchIndex index, params string[] documentJson)
    {
        var documents = new List<Document>();

        foreach (var json in documentJson)
        {
            var item = JsonNode.Parse(json)!.AsObject();
            var document = new Document();

            foreach (var field in index.Fields)
            {
                var value = item.FirstOrDefault(p =>
                    string.Equals(p.Key, field.Name, StringComparison.OrdinalIgnoreCase)).Value;

                if (value is null)
                {
                    continue;
                }

                foreach (var luceneField in field.CreateFields(value, index))
                {
                    document.Add(luceneField);
                }
            }

            documents.Add(document);
        }

        return documents;
    }

    private static readonly string[] Cities =
    [
        """{ "id": "1", "city": "Las Vegas",  "code": "Nevada", "tags": ["Casino", "DESERT"], "plain": "Las Vegas" }""",
        """{ "id": "2", "city": "LAS VEGAS",  "code": "NEVÁDA", "tags": ["casino"],           "plain": "LAS VEGAS" }""",
        """{ "id": "3", "city": "las vegas",  "code": "nevada", "tags": ["Desert"],           "plain": "las vegas" }""",
        """{ "id": "4", "city": "Seattle",    "code": "Washington", "tags": ["rain"],         "plain": "Seattle" }""",
    ];

    private static async Task<SearchResponse> Search(SearchIndex index, SearchRequest request)
    {
        using var helper = new LuceneTestHelper(index, CreateDocuments(index, Cities));
        var searcher = new LuceneNetIndexSearcher(new StubIndexReaderFactory(helper.Directory));

        return await searcher.Search(index, request);
    }

    private static IEnumerable<string> Ids(SearchResponse response)
        => response.Results.Select(i => i["id"]!.GetValue<string>());

    /// <summary>
    /// The scenario the Azure documentation opens with: a filter that would otherwise match
    /// only the exact casing matches every variant.
    /// </summary>
    [Theory]
    [InlineData("city eq 'las vegas'")]
    [InlineData("city eq 'Las Vegas'")]
    [InlineData("city eq 'LAS VEGAS'")]
    public async Task Filter_MatchesEveryCasingOfTheValue(string filter)
    {
        var response = await Search(CreateIndex(), new SearchRequest { Filter = filter, Top = 10 });

        Assert.Equal(["1", "2", "3"], Ids(response).Order());
    }

    /// <summary>
    /// A field with no normalizer keeps Azure's default behaviour, which is an exact match.
    /// </summary>
    /// <remarks>
    /// Sits beside the test above deliberately: together they show the folding comes from the
    /// normalizer rather than from some general loosening of string comparison.
    /// </remarks>
    [Fact]
    public async Task Filter_OnFieldWithoutNormalizer_StaysExact()
    {
        var response = await Search(CreateIndex(), new SearchRequest
        {
            Filter = "plain eq 'las vegas'",
            Top = 10,
        });

        Assert.Equal(["3"], Ids(response));
    }

    /// <summary>
    /// The standard normalizer folds accents as well as case, on both sides of the comparison.
    /// </summary>
    [Fact]
    public async Task Filter_FoldsAccentsWhenTheNormalizerDoes()
    {
        var response = await Search(CreateIndex(), new SearchRequest { Filter = "code eq 'nevada'", Top = 10 });

        Assert.Equal(["1", "2", "3"], Ids(response).Order());
    }

    [Fact]
    public async Task Filter_OnCollection_NormalizesEachElement()
    {
        var response = await Search(CreateIndex(), new SearchRequest
        {
            Filter = "tags/any(t: t eq 'desert')",
            Top = 10,
        });

        Assert.Equal(["1", "3"], Ids(response).Order());
    }

    [Fact]
    public async Task SearchIn_NormalizesEachListedValue()
    {
        var response = await Search(CreateIndex(), new SearchRequest
        {
            Filter = "search.in(city, 'LAS VEGAS, seattle', ',')",
            Top = 10,
        });

        Assert.Equal(["1", "2", "3", "4"], Ids(response).Order());
    }

    /// <summary>
    /// A range compares against the same normalized copy an equality does, so its bounds are
    /// folded too.
    /// </summary>
    /// <remarks>
    /// Worth its own test because a range reads a different Lucene field from an equality — the
    /// unanalyzed sidecar — and normalizing one copy but not the other would leave this the one
    /// comparison that still matched on casing.
    /// </remarks>
    [Fact]
    public async Task Range_ComparesAgainstNormalizedValues()
    {
        var response = await Search(CreateIndex(), new SearchRequest
        {
            // Every "las vegas" variant normalizes below "m", and Seattle's "seattle" above it.
            Filter = "city lt 'M'",
            Top = 10,
        });

        Assert.Equal(["1", "2", "3"], Ids(response).Order());
    }

    /// <summary>
    /// Facets count the folded value, so the three spellings form one bucket rather than three.
    /// </summary>
    [Fact]
    public async Task Facet_CountsNormalizedValuesAsOneBucket()
    {
        var response = await Search(CreateIndex(), new SearchRequest
        {
            Search = "*",
            Facets = ["city"],
            Top = 10,
        });

        var facets = response.Facets!["city"];

        Assert.Equal(
            [("las vegas", 3), ("seattle", 1)],
            facets.Select(i => (i.Value!.ToString(), i.Count)).Order());
    }

    /// <summary>
    /// Sorting orders by the folded value, which is what puts the variants together.
    /// </summary>
    /// <remarks>
    /// Lucene orders terms by their bytes, so without a normalizer "LAS VEGAS" and "Las Vegas"
    /// sort before "Seattle" while "las vegas" sorts after it — the interleaving Azure's
    /// documentation gives as the reason sorting needs one.
    /// </remarks>
    [Fact]
    public async Task Sort_OrdersByTheNormalizedValue()
    {
        var response = await Search(CreateIndex(), new SearchRequest
        {
            Search = "*",
            Orderby = "city asc, id asc",
            Top = 10,
        });

        Assert.Equal(["1", "2", "3", "4"], Ids(response));
    }

    /// <summary>
    /// A normalizer changes what a field is compared on, never what it returns.
    /// </summary>
    /// <remarks>
    /// Azure returns the value the document supplied, and a client reading a result back
    /// expects the text it wrote. Covers both shapes: <c>city</c> is searchable, so its stored
    /// copy rides along with the analyzed one, while <c>code</c> is not, so the indexing path
    /// has to write the stored copy separately from the normalized term.
    /// </remarks>
    [Fact]
    public async Task Retrieval_ReturnsTheOriginalValueNotTheNormalizedOne()
    {
        var response = await Search(CreateIndex(), new SearchRequest { Filter = "id eq '2'", Top = 10 });

        var document = Assert.Single(response.Results);

        Assert.Equal("LAS VEGAS", document["city"]!.GetValue<string>());
        Assert.Equal("NEVÁDA", document["code"]!.GetValue<string>());
    }

    /// <summary>
    /// Full-text search still runs through the field's analyzer, unaffected by the normalizer.
    /// </summary>
    /// <remarks>
    /// The two are independent in Azure, and a field may carry both. If normalization leaked
    /// into the analyzed copy, a searchable field would be indexed through two chains at once.
    /// </remarks>
    [Fact]
    public async Task Search_IsUnaffectedByTheNormalizer()
    {
        var response = await Search(CreateIndex(), new SearchRequest { Search = "Seattle", Top = 10 });

        Assert.Equal(["4"], Ids(response));
    }

    private class StubIndexReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);

        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);

        public void ClearCachedReader(string indexName) { }
    }
}
