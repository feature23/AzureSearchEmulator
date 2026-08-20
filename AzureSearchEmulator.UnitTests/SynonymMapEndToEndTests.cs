using System.Text.Json;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Xunit;
using SynonymMap = AzureSearchEmulator.Models.SynonymMap;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Exercises synonym maps through the real indexing and search path (issue #69).
/// </summary>
/// <remarks>
/// This is the part of the feature that only a search can prove. The documents are indexed with
/// no synonym applied — Azure expands at query time only — so a match here can come from nothing
/// but the query having been widened, and a test that passed while the expansion silently did
/// nothing would have to match on a term the document does not contain.
///
/// Every test names the map through a field's <c>synonymMaps</c> and resolves it through the
/// repository, so the wiring the controller depends on is covered too rather than being stubbed
/// past. See <see cref="SynonymMapTests"/> for the rules in isolation.
/// </remarks>
public class SynonymMapEndToEndTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private const string IndexJson =
        """
        {
          "name": "products",
          "fields": [
            { "name": "id", "type": "Edm.String", "key": true },
            { "name": "name", "type": "Edm.String", "searchable": true, "synonymMaps": ["products"] },
            { "name": "description", "type": "Edm.String", "searchable": true }
          ]
        }
        """;

    private static readonly string[] Products =
    [
        """{ "id": "1", "name": "canine chew toy",  "description": "for a canine" }""",
        """{ "id": "2", "name": "united states map", "description": "a map" }""",
        """{ "id": "3", "name": "feline scratcher",  "description": "for a feline" }""",
    ];

    private static SearchIndex CreateIndex() => JsonSerializer.Deserialize<SearchIndex>(IndexJson, Options)!;

    private static SynonymMap CreateMap(string synonyms) =>
        new() { Name = "products", Synonyms = synonyms };

    /// <summary>
    /// Builds documents through the real indexing path, so the tests read the same Lucene
    /// fields an uploaded document would produce.
    /// </summary>
    private static List<Document> CreateDocuments(SearchIndex index)
    {
        var documents = new List<Document>();

        foreach (var json in Products)
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

    private static async Task<List<string>> Search(SynonymMap? synonymMap, string search)
    {
        var index = CreateIndex();

        using var helper = new LuceneTestHelper(index, CreateDocuments(index));

        var searcher = new LuceneNetIndexSearcher(
            new StubIndexReaderFactory(helper.Directory),
            new StubSynonymMapRepository(synonymMap));

        var response = await searcher.Search(index, new SearchRequest { Search = search, Top = 10 });

        return response.Results.Select(i => i["id"]!.GetValue<string>()).Order().ToList();
    }

    /// <summary>
    /// The scenario Azure's documentation opens with: a query for a word the document does not
    /// contain matches through an equivalency rule.
    /// </summary>
    [Fact]
    public async Task EquivalencyRule_MatchesADocumentWithTheOtherTerm()
    {
        Assert.Equal(["1"], await Search(CreateMap("dog, canine"), "dog"));
    }

    /// <summary>
    /// Sits beside the test above deliberately: without the map the same query matches nothing,
    /// so the match there comes from the expansion rather than from any looser matching.
    /// </summary>
    [Fact]
    public async Task WithoutTheMap_TheSameQueryMatchesNothing()
    {
        Assert.Empty(await Search(null, "dog"));
    }

    /// <summary>
    /// Expansion runs in both directions, so the term the document holds still matches.
    /// </summary>
    [Fact]
    public async Task EquivalencyRule_StillMatchesTheOriginalTerm()
    {
        Assert.Equal(["1"], await Search(CreateMap("dog, canine"), "canine"));
    }

    /// <summary>
    /// A mapping rule replaces the query's term, so a document holding only the replacement
    /// matches and one holding only the original no longer does.
    /// </summary>
    [Fact]
    public async Task MappingRule_SearchesForTheReplacement()
    {
        Assert.Equal(["3"], await Search(CreateMap("cat => feline"), "cat"));
    }

    /// <summary>
    /// A multi-word rule matches as a phrase.
    /// </summary>
    [Fact]
    public async Task MultiWordRule_MatchesThePhrase()
    {
        Assert.Equal(["2"], await Search(CreateMap("usa, united states"), "usa"));
    }

    /// <summary>
    /// A field that names no map is unaffected, even while another field on the same index is
    /// expanding — which is what makes the map a property of the field rather than the index.
    /// </summary>
    [Fact]
    public async Task FieldWithoutAMap_DoesNotExpand()
    {
        var index = CreateIndex();

        using var helper = new LuceneTestHelper(index, CreateDocuments(index));

        var searcher = new LuceneNetIndexSearcher(
            new StubIndexReaderFactory(helper.Directory),
            new StubSynonymMapRepository(CreateMap("dog, canine")));

        var response = await searcher.Search(index, new SearchRequest
        {
            Search = "dog",
            SearchFields = "description",
            Top = 10,
        });

        Assert.Empty(response.Results);
    }

    /// <summary>
    /// A query is widened by the rules the map holds now, not the ones it held when the
    /// documents were indexed — the property that makes a map safe to edit.
    /// </summary>
    [Fact]
    public async Task EditedMap_TakesEffectWithoutReindexing()
    {
        Assert.Empty(await Search(CreateMap("bird, avian"), "dog"));
        Assert.Equal(["1"], await Search(CreateMap("dog, canine"), "dog"));
    }

    /// <summary>
    /// A field naming a map the service no longer holds searches unexpanded rather than
    /// failing, for the reason given on <see cref="SynonymMapHelper.Resolve"/>.
    /// </summary>
    [Fact]
    public async Task MissingMap_LeavesTheQueryUnexpanded()
    {
        Assert.Equal(["1"], await Search(null, "canine"));
    }

    private class StubIndexReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);

        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);

        public void ClearCachedReader(string indexName) { }
    }

    private class StubSynonymMapRepository(SynonymMap? synonymMap) : ISynonymMapRepository
    {
        public async IAsyncEnumerable<SynonymMap> GetAll()
        {
            if (synonymMap != null)
            {
                yield return synonymMap;
            }

            await Task.CompletedTask;
        }

        public Task<SynonymMap?> Get(string key) => Task.FromResult(synonymMap);

        public Task Create(SynonymMap map) => Task.CompletedTask;

        public Task Update(SynonymMap map) => Task.CompletedTask;

        public Task<bool> Delete(SynonymMap map) => Task.FromResult(true);
    }
}
