using Azure.Search.Documents.Models;
using AzureSearchEmulator.Dashboard;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests for the pieces behind the dashboard's search page (issue #92) that do not need a running
/// emulator: how its inputs become SDK options, how it reaches itself, and how it renders JSON and
/// highlights.
/// </summary>
public class SearchPageTests
{
    [Theory]
    [InlineData(new[] { "http://localhost:5123" }, "http://localhost:5123/")]
    [InlineData(new[] { "https://+:8443" }, "https://localhost:8443/")]
    [InlineData(new[] { "http://*:80" }, "http://localhost/")]
    [InlineData(new[] { "http://0.0.0.0:8080" }, "http://127.0.0.1:8080/")]
    [InlineData(new[] { "http://[::]:8080" }, "http://localhost:8080/")]
    [InlineData(new[] { "https://localhost:5001", "http://localhost:5000" }, "http://localhost:5000/")]
    public void LoopbackEndpoint_ConnectsToABinding(string[] addresses, string expected)
    {
        Assert.Equal(new Uri(expected), LoopbackEndpoint.Resolve(addresses));
    }

    [Fact]
    public void LoopbackEndpoint_WithNoUsableBinding_IsNull()
    {
        Assert.Null(LoopbackEndpoint.Resolve([]));
        Assert.Null(LoopbackEndpoint.Resolve(["unix:/tmp/emulator.sock"]));
    }

    [Fact]
    public void SplitList_KeepsCommasInsideFunctionsAndQuotes()
    {
        var items = SearchQueryForm.SplitList("geo.distance(location, geography'POINT(-122 47)') asc, rating desc,, name ");

        Assert.Equal(["geo.distance(location, geography'POINT(-122 47)') asc", "rating desc", "name"], items);
    }

    [Fact]
    public void SplitFacets_KeepsParametersWithTheirField()
    {
        var facets = SearchQueryForm.SplitFacets("category, rating,interval:1, tags,count:5,sort:value");

        Assert.Equal(["category", "rating,interval:1", "tags,count:5,sort:value"], facets);
    }

    [Fact]
    public void ToSearchOptions_OmitsBlankInputs()
    {
        var options = new SearchQueryForm { Filter = "  " }.ToSearchOptions();

        Assert.Null(options.Filter);
        Assert.Null(options.Skip);
        Assert.Null(options.SemanticSearch);
        Assert.Empty(options.OrderBy);
        Assert.Empty(options.Facets);
        Assert.Equal(SearchQueryForm.DefaultTop, options.Size);
        Assert.True(options.IncludeTotalCount);
    }

    [Fact]
    public void ToSearchOptions_CarriesEveryInput()
    {
        var form = new SearchQueryForm
        {
            QueryType = "semantic",
            SearchMode = "all",
            Filter = "rating ge 4",
            OrderBy = "rating desc, name",
            Select = "id, name",
            SearchFields = "name",
            Facets = "category",
            Highlight = "description",
            ScoringProfile = "boost",
            SemanticConfiguration = "default",
            Top = 5,
            Skip = 10,
            IncludeTotalCount = false
        };

        var options = form.ToSearchOptions();

        Assert.Equal(SearchQueryType.Semantic, options.QueryType);
        Assert.Equal(SearchMode.All, options.SearchMode);
        Assert.Equal("rating ge 4", options.Filter);
        Assert.Equal(["rating desc", "name"], options.OrderBy);
        Assert.Equal(["id", "name"], options.Select);
        Assert.Equal(["name"], options.SearchFields);
        Assert.Equal(["category"], options.Facets);
        Assert.Equal(["description"], options.HighlightFields);
        Assert.Equal("boost", options.ScoringProfile);
        Assert.Equal("default", options.SemanticSearch.SemanticConfigurationName);
        Assert.Equal(5, options.Size);
        Assert.Equal(10, options.Skip);
        Assert.False(options.IncludeTotalCount);
    }

    [Fact]
    public void JsonHighlighter_ClassifiesTokens()
    {
        var tokens = JsonHighlighter.Tokenize("""{"name": "Fancy \"Inn\"", "rating": -4.5e1, "open": true, "tags": null}""");

        Assert.Contains(new JsonToken(JsonTokenKind.Key, "\"name\""), tokens);
        Assert.Contains(new JsonToken(JsonTokenKind.String, "\"Fancy \\\"Inn\\\"\""), tokens);
        Assert.Contains(new JsonToken(JsonTokenKind.Number, "-4.5e1"), tokens);
        Assert.Contains(new JsonToken(JsonTokenKind.Boolean, "true"), tokens);
        Assert.Contains(new JsonToken(JsonTokenKind.Null, "null"), tokens);
        Assert.Contains(new JsonToken(JsonTokenKind.Punctuation, "{"), tokens);
    }

    [Theory]
    [InlineData("""{"a": [1, 2, {"b": "c"}]}""")]
    [InlineData("""{"unterminated": "oops""")]
    [InlineData("not json at all")]
    public void JsonHighlighter_TokensReproduceTheInput(string json)
    {
        var tokens = JsonHighlighter.Tokenize(json);

        Assert.Equal(json, string.Concat(tokens.Select(i => i.Text)));
    }

    [Fact]
    public void JsonHighlighter_Format_IndentsWithoutEscapingMarkup()
    {
        var formatted = JsonHighlighter.Format("""{"text":"a <em>b</em>"}""");

        Assert.Equal("{\n  \"text\": \"a <em>b</em>\"\n}", formatted.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void JsonHighlighter_Format_LeavesInvalidJsonAlone()
    {
        Assert.Equal("<html>oops</html>", JsonHighlighter.Format("<html>oops</html>"));
    }

    [Fact]
    public void HighlightMarkup_EncodesDocumentTextButKeepsEmphasis()
    {
        var markup = HighlightMarkup.Render("<script>x</script> the <em>hotel</em> & spa");

        Assert.Equal("&lt;script&gt;x&lt;/script&gt; the <mark>hotel</mark> &amp; spa", markup.Value);
    }

    [Fact]
    public void DocumentValueFormatter_SummarisesVectors()
    {
        var vector = Enumerable.Range(0, 1536).Select(i => (object)(double)i).ToArray();

        Assert.Equal("[1536 numbers]", DocumentValueFormatter.Format(vector));
        Assert.Equal("[1,2,3]", DocumentValueFormatter.Format(new object[] { 1, 2, 3 }));
        Assert.Equal("[\"a\",\"b\"]", DocumentValueFormatter.Format(new object[] { "a", "b" }));
        Assert.Equal("4.5", DocumentValueFormatter.Format(4.5));
        Assert.Equal("null", DocumentValueFormatter.Format(null));
    }
}
