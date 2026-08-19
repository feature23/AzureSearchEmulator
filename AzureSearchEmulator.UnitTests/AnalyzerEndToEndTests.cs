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
/// Exercises a custom analyzer through the real indexing and search path, rather than by
/// tokenizing directly (issue #34).
/// </summary>
/// <remarks>
/// Tokenizing an analyzer proves it was built correctly; it does not prove the search path uses
/// it. Both sides have to resolve the same definition — the index writer to store the terms and
/// the query parser to produce them — and a mismatch shows up only here, as a query that finds
/// nothing.
/// </remarks>
public class AnalyzerEndToEndTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CustomAnalyzer_GovernsWhatMatches()
    {
        var index = JsonSerializer.Deserialize<SearchIndex>(
            """
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "title", "type": "Edm.String", "searchable": true, "analyzer": "folding" }
              ],
              "analyzers": [
                {
                  "name": "folding",
                  "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                  "tokenizer": "standard_v2",
                  "tokenFilters": ["lowercase", "asciifolding"],
                  "charFilters": ["html_strip"]
                }
              ]
            }
            """,
            Options)!;

        var document = new Document();

        foreach (var field in index.Fields)
        {
            JsonNode value = field.Name == "id" ? "1" : "<b>CAFÉ</b> Noir";

            foreach (var luceneField in field.CreateFields(value))
            {
                document.Add(luceneField);
            }
        }

        using var helper = new LuceneTestHelper(index, [document]);
        var searcher = new LuceneNetIndexSearcher(new StubIndexReaderFactory(helper.Directory));

        // Matches only because the whole chain ran at index time and the query was analyzed the
        // same way: html_strip dropped the markup, lowercase and asciifolding turned CAFÉ into
        // "cafe".
        var response = await searcher.Search(index, new SearchRequest { Search = "cafe", Top = 10 });

        Assert.Single(response.Results);
    }

    private class StubIndexReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);

        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);

        public void ClearCachedReader(string indexName) { }
    }
}
