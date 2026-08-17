using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for the suggest and autocomplete searching path (issue #45).
/// </summary>
/// <remarks>
/// The product corpus these run against has two documents whose Name starts with "Laptop"
/// ("Laptop Pro 15" and "Laptop Budget 13"), whose Descriptions both contain "laptop", plus a
/// keyboard whose Description contains "lighting" — near enough to "laptop" to catch a prefix
/// query that has quietly become a fuzzy one.
/// </remarks>
public class SuggestAndAutocompleteTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly LuceneNetIndexSearcher _searcher;

    public SuggestAndAutocompleteTests()
    {
        var index = LuceneTestHelper.CreateSuggesterProductIndex();
        _helper = new LuceneTestHelper(index, LuceneTestHelper.CreateProductDocuments());
        _searcher = new LuceneNetIndexSearcher(new StubIndexReaderFactory(_helper.Directory));
    }

    public void Dispose() => _helper.Dispose();

    private SearchIndex Index => _helper.Index;

    // ===== Suggest =====

    [Fact]
    public async Task Suggest_PartialTerm_ReturnsMatchingDocuments()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "lap",
            SuggesterName = "sg",
        });

        Assert.Equal(2, response.Results.Count);
        Assert.All(response.Results, i =>
            Assert.Contains("Laptop", i["@search.text"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Suggest_SuggestionTextComesFromTheMatchingSourceField()
    {
        // "precision" appears only in the Gaming Mouse's Description, so the suggestion must
        // show the Description rather than the Name that field order would otherwise favour.
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "precisio",
            SuggesterName = "sg",
        });

        var result = Assert.Single(response.Results);
        Assert.Equal("Precision gaming mouse with 16000 DPI sensor", result["@search.text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Suggest_IsAPrefixMatchRatherThanASubstringOne()
    {
        // "aptop" is inside "Laptop" but does not start it, so nothing should match.
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "aptop",
            SuggesterName = "sg",
        });

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Suggest_MultipleTerms_RequiresEveryTermToMatch()
    {
        // "laptop" is in both laptop Descriptions, but "students" only in the budget one.
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "laptop stud",
            SuggesterName = "sg",
        });

        var result = Assert.Single(response.Results);
        Assert.Contains("students", result["@search.text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Suggest_Top_LimitsTheNumberOfSuggestions()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            Top = 1,
        });

        Assert.Single(response.Results);
    }

    [Fact]
    public async Task Suggest_Filter_NarrowsTheSuggestions()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            Filter = "Price lt 1000",
        });

        var result = Assert.Single(response.Results);
        Assert.Equal("Laptop Budget 13", result["@search.text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Suggest_Select_NarrowsTheReturnedFields()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            Select = "Id",
        });

        Assert.All(response.Results, i =>
        {
            Assert.True(i.ContainsKey("Id"));
            Assert.False(i.ContainsKey("Name"));
            // The suggestion text is not a document field, so $select never removes it.
            Assert.True(i.ContainsKey("@search.text"));
        });
    }

    [Fact]
    public async Task Suggest_Orderby_SortsTheSuggestions()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            Orderby = "Price asc",
        });

        Assert.Equal("Laptop Budget 13", response.Results[0]["@search.text"]!.GetValue<string>());
        Assert.Equal("Laptop Pro 15", response.Results[1]["@search.text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Suggest_HighlightTags_WrapTheMatchedText()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            HighlightPreTag = "<b>",
            HighlightPostTag = "</b>",
            Top = 1,
            Orderby = "Price asc",
        });

        var result = Assert.Single(response.Results);
        // Only the typed prefix is wrapped, not the whole word it landed in.
        Assert.Equal("<b>Lap</b>top Budget 13", result["@search.text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Suggest_WithoutHighlightTags_LeavesTheTextAlone()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            Top = 1,
            Orderby = "Price asc",
        });

        var result = Assert.Single(response.Results);
        Assert.Equal("Laptop Budget 13", result["@search.text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Suggest_Fuzzy_ToleratesAMisspellingInACompletedTerm()
    {
        // "laptop" misspelled by one character, followed by a partial term. Only the budget
        // laptop's Description carries "students", so it alone should survive the misspelling.
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "laptob stud",
            SuggesterName = "sg",
            Fuzzy = true,
            Select = "Id",
        });

        var result = Assert.Single(response.Results);
        Assert.Equal("2", result["Id"]!.GetValue<string>());
        // No source field contains the misspelled term literally, so the suggestion falls back
        // to the first populated one rather than coming back with empty text.
        Assert.Equal("Laptop Budget 13", result["@search.text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Suggest_WithoutFuzzy_DoesNotTolerateAMisspelling()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "laptob stud",
            SuggesterName = "sg",
        });

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Suggest_SearchFields_NarrowsToOneSourceField()
    {
        // "precision" is in a Description only, so restricting to Name finds nothing.
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "precisio",
            SuggesterName = "sg",
            SearchFields = "Name",
        });

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Suggest_EmptySearch_ReturnsNothing()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "",
            SuggesterName = "sg",
        });

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Suggest_MinimumCoverage_ReportsFullCoverage()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            MinimumCoverage = 50,
        });

        Assert.Equal(SearchCoverage.Full, response.Coverage);
    }

    [Fact]
    public async Task Suggest_WithoutMinimumCoverage_OmitsCoverage()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "lap",
            SuggesterName = "sg",
        });

        Assert.Null(response.Coverage);
    }

    // ===== Autocomplete =====

    [Fact]
    public async Task Autocomplete_OneTerm_CompletesTheTypedWord()
    {
        var response = await _searcher.Autocomplete(Index, new AutocompleteRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            AutocompleteMode = AutocompleteModes.OneTerm,
        });

        var item = Assert.Single(response.Results);
        Assert.Equal("laptop", item.Text);
        Assert.Equal("laptop", item.QueryPlusText);
    }

    [Fact]
    public async Task Autocomplete_TwoTerms_AppendsTheFollowingWord()
    {
        var response = await _searcher.Autocomplete(Index, new AutocompleteRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            AutocompleteMode = AutocompleteModes.TwoTerms,
            Top = 100,
        });

        Assert.All(response.Results, i => Assert.StartsWith("laptop ", i.Text));
        Assert.Contains(response.Results, i => i.Text == "laptop pro");
    }

    [Fact]
    public async Task Autocomplete_OneTermWithContext_RequiresThePrecedingWordToMatch()
    {
        // "Affordable laptop" occurs; "gaming laptop" does not.
        var matching = await _searcher.Autocomplete(Index, new AutocompleteRequest
        {
            Search = "affordable lap",
            SuggesterName = "sg",
            AutocompleteMode = AutocompleteModes.OneTermWithContext,
        });

        var item = Assert.Single(matching.Results);
        Assert.Equal("laptop", item.Text);

        var nonMatching = await _searcher.Autocomplete(Index, new AutocompleteRequest
        {
            Search = "gaming lap",
            SuggesterName = "sg",
            AutocompleteMode = AutocompleteModes.OneTermWithContext,
        });

        Assert.Empty(nonMatching.Results);
    }

    [Fact]
    public async Task Autocomplete_QueryPlusTextKeepsTheCompletedTermsAsTyped()
    {
        var response = await _searcher.Autocomplete(Index, new AutocompleteRequest
        {
            Search = "Affordable lap",
            SuggesterName = "sg",
            AutocompleteMode = AutocompleteModes.OneTermWithContext,
        });

        var item = Assert.Single(response.Results);
        Assert.Equal("Affordable laptop", item.QueryPlusText);
    }

    [Fact]
    public async Task Autocomplete_ReturnsDistinctCompletions()
    {
        // "laptop" occurs in four documents' fields, but is one completion.
        var response = await _searcher.Autocomplete(Index, new AutocompleteRequest
        {
            Search = "lapt",
            SuggesterName = "sg",
            Top = 100,
        });

        Assert.Single(response.Results);
    }

    [Fact]
    public async Task Autocomplete_Top_LimitsTheNumberOfCompletions()
    {
        var response = await _searcher.Autocomplete(Index, new AutocompleteRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            AutocompleteMode = AutocompleteModes.TwoTerms,
            Top = 2,
        });

        Assert.Equal(2, response.Results.Count);
    }

    [Fact]
    public async Task Autocomplete_Filter_NarrowsTheCompletions()
    {
        var response = await _searcher.Autocomplete(Index, new AutocompleteRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            AutocompleteMode = AutocompleteModes.TwoTerms,
            Filter = "Price lt 1000",
            Top = 100,
        });

        Assert.DoesNotContain(response.Results, i => i.Text == "laptop pro");
        Assert.Contains(response.Results, i => i.Text == "laptop budget");
    }

    [Fact]
    public async Task Autocomplete_HighlightTags_WrapTheTypedPrefix()
    {
        var response = await _searcher.Autocomplete(Index, new AutocompleteRequest
        {
            Search = "lap",
            SuggesterName = "sg",
            HighlightPreTag = "<b>",
            HighlightPostTag = "</b>",
        });

        var item = Assert.Single(response.Results);
        Assert.Equal("<b>lap</b>top", item.Text);
    }

    [Fact]
    public async Task Autocomplete_EmptySearch_ReturnsNothing()
    {
        var response = await _searcher.Autocomplete(Index, new AutocompleteRequest
        {
            Search = "   ",
            SuggesterName = "sg",
        });

        Assert.Empty(response.Results);
    }

    // ===== Suggester resolution =====

    [Fact]
    public async Task Suggest_UnknownSuggester_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _searcher.Suggest(Index, new SuggestRequest { Search = "lap", SuggesterName = "nope" }));

        Assert.Contains("does not have a suggester named 'nope'", ex.Message);
    }

    [Fact]
    public async Task Suggest_MissingSuggesterName_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _searcher.Suggest(Index, new SuggestRequest { Search = "lap" }));

        Assert.Contains("suggesterName", ex.Message);
    }

    [Fact]
    public async Task Autocomplete_UnknownSuggester_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _searcher.Autocomplete(Index, new AutocompleteRequest { Search = "lap", SuggesterName = "nope" }));
    }

    [Fact]
    public async Task Suggest_SuggesterNameIsMatchedCaseInsensitively()
    {
        var response = await _searcher.Suggest(Index, new SuggestRequest
        {
            Search = "lap",
            SuggesterName = "SG",
        });

        Assert.NotEmpty(response.Results);
    }

    [Fact]
    public async Task Suggest_SearchFieldsOutsideTheSuggester_Throws()
    {
        // Category is a real field, but not one this suggester draws from, so it is refused
        // rather than quietly returning suggestions Azure would not.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _searcher.Suggest(Index, new SuggestRequest
            {
                Search = "lap",
                SuggesterName = "sg",
                SearchFields = "Category",
            }));

        Assert.Contains("not a source field", ex.Message);
    }

    private class StubIndexReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);

        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);

        public void ClearCachedReader(string indexName) { }
    }
}
