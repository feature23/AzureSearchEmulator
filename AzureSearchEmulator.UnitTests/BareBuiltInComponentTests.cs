using System.Text.Json;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for naming a built-in analysis component without defining it (issue #73).
/// </summary>
/// <remarks>
/// Azure lets a custom analyzer name a built-in on its own to mean "use it with its defaults".
/// Most components already worked that way here, because the Lucene factory defaults the same
/// options; these cover the ones where Lucene instead demands an argument Azure documents a
/// default for, and the bare form of a valid Azure definition was rejected at index creation.
///
/// The assertions compare the bare form against the equivalent explicit definition rather than
/// against a fixed token list, since that is the property that matters: the two spellings are
/// the same analyzer, so a default that drifted from Azure's would show up as a difference
/// between them.
/// </remarks>
public class BareBuiltInComponentTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static List<string> Tokenize(Analyzer analyzer, string text)
    {
        var tokens = new List<string>();

        using var stream = analyzer.GetTokenStream("field", text);
        var term = stream.AddAttribute<ICharTermAttribute>();

        stream.Reset();

        while (stream.IncrementToken())
        {
            tokens.Add(term.ToString());
        }

        stream.End();

        return tokens;
    }

    private static SearchIndex CreateIndex(string analyzersJson, string? componentsJson = null)
    {
        var json = $$"""
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "text", "type": "Edm.String", "searchable": true }
              ],
              {{(componentsJson == null ? "" : componentsJson + ",")}}
              "analyzers": [{{analyzersJson}}]
            }
            """;

        return JsonSerializer.Deserialize<SearchIndex>(json, Options)!;
    }

    private static Analyzer Build(string analyzersJson, string? componentsJson = null)
    {
        var index = CreateIndex(analyzersJson, componentsJson);

        return AnalyzerHelper.GetAnalyzer(index, "mine");
    }

    /// <summary>
    /// The failure reported in issue #73: the bare <c>pattern</c> tokenizer was the one built-in
    /// name that could not be used without a definition.
    /// </summary>
    [Fact]
    public void BarePatternTokenizer_IsAccepted()
    {
        var analyzer = Build("""
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "pattern" }
            """);

        // Azure's default splits on runs of non-word characters.
        Assert.Equal(["Hello", "world", "foo", "don", "t"], Tokenize(analyzer, "Hello world-foo don't"));
    }

    /// <summary>
    /// The equivalence the issue asks for: the bare form has to behave as the explicit
    /// <c>\W+</c> definition does, not merely stop throwing.
    /// </summary>
    [Fact]
    public void BarePatternTokenizer_MatchesExplicitDefinition()
    {
        var bare = Build("""
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "pattern" }
            """);

        var explicitly = Build(
            """
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "tk" }
            """,
            """
            "tokenizers": [
              { "name": "tk", "@odata.type": "#Microsoft.Azure.Search.PatternTokenizer",
                "pattern": "\\W+", "group": -1 }
            ]
            """);

        const string text = "Hello world-foo, don't stop_now 12.34";

        Assert.Equal(Tokenize(explicitly, text), Tokenize(bare, text));
    }

    /// <summary>
    /// A definition's own options still win over the defaults seeded for the bare form.
    /// </summary>
    [Fact]
    public void PatternTokenizerDefinition_OverridesTheDefaultPattern()
    {
        var analyzer = Build(
            """
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "tk" }
            """,
            """
            "tokenizers": [
              { "name": "tk", "@odata.type": "#Microsoft.Azure.Search.PatternTokenizer",
                "pattern": "," }
            ]
            """);

        // Splitting on commas only, so the spaces and the hyphen survive inside the tokens.
        Assert.Equal(["a b", "c-d"], Tokenize(analyzer, "a b,c-d"));
    }

    /// <summary>
    /// The other two components the audit for this issue found: Azure documents a default for
    /// every option they take, so the bare form is a valid definition there too.
    /// </summary>
    [Fact]
    public void BareLengthTokenFilter_IsAcceptedAndKeepsTokensUpToTheAzureMaximum()
    {
        var analyzer = Build("""
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "whitespace", "tokenFilters": ["length"] }
            """);

        // Azure defaults to min 0 / max 300, so ordinary words all pass.
        Assert.Equal(["a", "bb", "ccc"], Tokenize(analyzer, "a bb ccc"));

        // A token longer than the 300 ceiling is the one the filter drops. Tokenized with
        // keyword, since whitespace caps a token at 255 characters and would split it first.
        var wholeInput = Build("""
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "keyword_v2", "tokenFilters": ["length"] }
            """);

        Assert.Equal([new string('x', 300)], Tokenize(wholeInput, new string('x', 300)));
        Assert.Empty(Tokenize(wholeInput, new string('x', 301)));
    }

    [Fact]
    public void BareLimitTokenFilter_IsAcceptedAndKeepsOneToken()
    {
        var analyzer = Build("""
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "whitespace", "tokenFilters": ["limit"] }
            """);

        // Azure documents a default maxTokenCount of 1.
        Assert.Equal(["one"], Tokenize(analyzer, "one two three"));
    }

    [Fact]
    public void LimitTokenFilterDefinition_OverridesTheDefaultCount()
    {
        var analyzer = Build(
            """
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "whitespace", "tokenFilters": ["lim"] }
            """,
            """
            "tokenFilters": [
              { "name": "lim", "@odata.type": "#Microsoft.Azure.Search.LimitTokenFilter",
                "maxTokenCount": 2 }
            ]
            """);

        Assert.Equal(["one", "two"], Tokenize(analyzer, "one two three"));
    }

    /// <summary>
    /// Every built-in tokenizer name, to catch another one regressing into the bare-form
    /// failure that <c>pattern</c> had.
    /// </summary>
    /// <remarks>
    /// The proprietary <c>microsoft_language_*</c> tokenizers are left out: they are rejected
    /// deliberately, and <c>AnalyzerTests</c> covers that.
    /// </remarks>
    [Theory]
    [InlineData("classic")]
    [InlineData("edgeNGram")]
    [InlineData("keyword_v2")]
    [InlineData("letter")]
    [InlineData("lowercase")]
    [InlineData("nGram")]
    [InlineData("path_hierarchy_v2")]
    [InlineData("pattern")]
    [InlineData("standard_v2")]
    [InlineData("uax_url_email")]
    [InlineData("whitespace")]
    public void BareBuiltInTokenizer_CanBeUsedWithoutADefinition(string tokenizer)
    {
        var analyzer = Build($$"""
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "{{tokenizer}}" }
            """);

        Assert.NotEmpty(Tokenize(analyzer, "Hello world-foo don't 123"));
    }

    /// <summary>
    /// The same sweep over the token filters whose options Azure all defaults.
    /// </summary>
    [Theory]
    [InlineData("apostrophe")]
    [InlineData("arabic_normalization")]
    [InlineData("asciifolding")]
    [InlineData("cjk_bigram")]
    [InlineData("cjk_width")]
    [InlineData("classic")]
    [InlineData("common_grams")]
    [InlineData("edgeNGram_v2")]
    [InlineData("elision")]
    [InlineData("german_normalization")]
    [InlineData("hindi_normalization")]
    [InlineData("indic_normalization")]
    [InlineData("keyword_repeat")]
    [InlineData("kstem")]
    [InlineData("length")]
    [InlineData("limit")]
    [InlineData("lowercase")]
    [InlineData("nGram_v2")]
    [InlineData("persian_normalization")]
    [InlineData("porter_stem")]
    [InlineData("reverse")]
    [InlineData("scandinavian_folding")]
    [InlineData("scandinavian_normalization")]
    [InlineData("shingle")]
    [InlineData("snowball")]
    [InlineData("sorani_normalization")]
    [InlineData("stemmer")]
    [InlineData("stopwords")]
    [InlineData("trim")]
    [InlineData("truncate")]
    [InlineData("unique")]
    [InlineData("uppercase")]
    [InlineData("word_delimiter")]
    public void BareBuiltInTokenFilter_CanBeUsedWithoutADefinition(string filter)
    {
        var analyzer = Build($$"""
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "whitespace", "tokenFilters": ["{{filter}}"] }
            """);

        // Only that the chain builds and runs; what each filter emits is its own business.
        Tokenize(analyzer, "Hello world-foo don't 123");
    }

    [Fact]
    public void BareHtmlStripCharFilter_CanBeUsedWithoutADefinition()
    {
        var analyzer = Build("""
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "whitespace", "charFilters": ["html_strip"] }
            """);

        Assert.Equal(["hello"], Tokenize(analyzer, "<b>hello</b>"));
    }

    /// <summary>
    /// The components Azure marks an option required on stay rejected — defaulting them would
    /// invent behaviour the service does not have — but the message now names what is missing
    /// instead of blaming a missing assembly.
    /// </summary>
    [Theory]
    [InlineData("pattern_replace", "pattern")]
    [InlineData("pattern_capture", "patterns")]
    [InlineData("synonym", "synonyms")]
    [InlineData("dictionary_decompounder", "wordList")]
    public void BareTokenFilterMissingARequiredOption_IsRejectedByName(string filter, string option)
    {
        var index = CreateIndex($$"""
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "whitespace", "tokenFilters": ["{{filter}}"] }
            """);

        var ex = Assert.Throws<AnalyzerDefinitionException>(
            () => AnalyzerHelper.GetAnalyzer(index, "mine"));

        Assert.Contains(filter, ex.Message);
        Assert.Contains(option, ex.Message);
        Assert.DoesNotContain("Assembly", ex.Message);
    }

    /// <summary>
    /// A definition that supplies options is not a bare reference, so it must not be told an
    /// option it spelled out is missing.
    /// </summary>
    /// <remarks>
    /// These components still fail to build for an unrelated reason — Azure supplies
    /// <c>wordList</c> and <c>synonyms</c> inline where Lucene's factories read them from a
    /// resource, which the emulator does not yet translate. The point here is only that the
    /// error does not claim the option was left out, which would send the reader to fix
    /// something already correct.
    /// </remarks>
    [Theory]
    [InlineData("dictionarycompoundword", "\"wordList\": [\"dampf\", \"schiff\"]")]
    [InlineData("synonym", "\"synonyms\": [\"car, automobile\"]")]
    public void DefinedComponentSupplyingItsRequiredOption_IsNotReportedAsMissingIt(
        string type,
        string optionJson)
    {
        // Named for its own SPI name so that the resolution reaches the factory, which is the
        // only place the missing-option message could be produced.
        var index = CreateIndex(
            $$"""
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "whitespace", "tokenFilters": ["{{type}}"] }
            """,
            $$"""
            "tokenFilters": [{ "name": "{{type}}", {{optionJson}} }]
            """);

        var ex = Assert.Throws<AnalyzerDefinitionException>(
            () => AnalyzerHelper.GetAnalyzer(index, "mine"));

        Assert.DoesNotContain("on its own", ex.Message);
        Assert.DoesNotContain("no default", ex.Message);
    }

    [Fact]
    public void BarePatternReplaceCharFilter_IsRejectedByName()
    {
        var index = CreateIndex("""
            { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
              "tokenizer": "whitespace", "charFilters": ["pattern_replace"] }
            """);

        var ex = Assert.Throws<AnalyzerDefinitionException>(
            () => AnalyzerHelper.GetAnalyzer(index, "mine"));

        Assert.Contains("pattern_replace", ex.Message);
        Assert.Contains("pattern", ex.Message);
        Assert.DoesNotContain("Assembly", ex.Message);
    }
}
