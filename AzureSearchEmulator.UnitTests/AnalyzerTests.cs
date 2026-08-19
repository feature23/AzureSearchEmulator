using System.Text.Json;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for predefined analyzer name coverage and custom analyzer definitions
/// (issue #34).
/// </summary>
/// <remarks>
/// Most assertions are on the tokens an analyzer produces rather than on the Lucene type it
/// resolves to. The type is an implementation detail — several Azure names deliberately share
/// one Lucene analyzer — whereas the tokens are what a search actually matches on.
/// </remarks>
public class AnalyzerTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs text through an analyzer and returns the terms it emits.
    /// </summary>
    private static List<string> Tokenize(Analyzer analyzer, string text, string field = "field")
    {
        var tokens = new List<string>();

        using var stream = analyzer.GetTokenStream(field, text);
        var term = stream.AddAttribute<ICharTermAttribute>();

        stream.Reset();

        while (stream.IncrementToken())
        {
            tokens.Add(term.ToString());
        }

        stream.End();

        return tokens;
    }

    private static SearchIndex CreateIndex(string? analyzersJson = null)
    {
        var json = $$"""
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "text", "type": "Edm.String", "searchable": true }
              ]
              {{(analyzersJson == null ? "" : "," + analyzersJson)}}
            }
            """;

        return JsonSerializer.Deserialize<SearchIndex>(json, Options)!;
    }

    /// <summary>
    /// The names that used to throw <see cref="NotSupportedException"/>, which is what made an
    /// index using them unusable.
    /// </summary>
    [Theory]
    [InlineData("en.microsoft")]
    [InlineData("de.microsoft")]
    [InlineData("fr.microsoft")]
    [InlineData("ja.microsoft")]
    [InlineData("ko.microsoft")]
    [InlineData("zh-Hans.microsoft")]
    [InlineData("zh-Hant.microsoft")]
    [InlineData("th.microsoft")]
    [InlineData("he.microsoft")]
    [InlineData("vi.microsoft")]
    [InlineData("sr-cyrillic.microsoft")]
    [InlineData("sr-latin.microsoft")]
    [InlineData("pattern")]
    [InlineData("stop")]
    [InlineData("standardasciifolding.lucene")]
    public void PredefinedName_Resolves(string name)
    {
        Assert.NotNull(AnalyzerHelper.TryCreatePredefined(name));
    }

    /// <summary>
    /// Azure's enum spells the Portuguese and Chinese names with a capitalized region, while its
    /// own prose documentation spells the Portuguese pair differently. The emulator matched only
    /// one spelling and threw on the other; both have to work.
    /// </summary>
    [Theory]
    [InlineData("pt-BR.lucene")]
    [InlineData("pt-Br.lucene")]
    [InlineData("pt-br.lucene")]
    [InlineData("PT-BR.LUCENE")]
    [InlineData("pt-PT.lucene")]
    [InlineData("pt-Pt.lucene")]
    [InlineData("zh-Hans.microsoft")]
    [InlineData("ZH-HANS.MICROSOFT")]
    public void RegionQualifiedName_MatchesRegardlessOfCase(string name)
    {
        Assert.NotNull(AnalyzerHelper.TryCreatePredefined(name));
    }

    [Fact]
    public void UnknownName_DoesNotResolve()
    {
        Assert.Null(AnalyzerHelper.TryCreatePredefined("no.such.analyzer"));
    }

    /// <summary>
    /// A name that is neither predefined nor defined by the index used to throw a
    /// <see cref="NotSupportedException"/> with no message at all, leaving the caller with no
    /// way to tell which analyzer was rejected.
    /// </summary>
    [Fact]
    public void UnknownName_ThrowsNamingTheAnalyzer()
    {
        var index = CreateIndex();

        var ex = Assert.Throws<AnalyzerDefinitionException>(
            () => AnalyzerHelper.GetAnalyzer(index, "no.such.analyzer"));

        Assert.Contains("no.such.analyzer", ex.Message);
    }

    /// <summary>
    /// The CJK languages are not space-delimited, so the fallback has to do better than treating
    /// a whole clause as one term.
    /// </summary>
    [Fact]
    public void CjkName_ProducesMoreThanOneToken()
    {
        var analyzer = AnalyzerHelper.TryCreatePredefined("ja.microsoft")!;

        var tokens = Tokenize(analyzer, "東京都");

        Assert.True(tokens.Count > 1, $"expected bigrams, got [{string.Join(", ", tokens)}]");
    }

    [Fact]
    public void StandardAsciiFolding_FoldsAccents()
    {
        var analyzer = AnalyzerHelper.TryCreatePredefined("standardasciifolding.lucene")!;

        Assert.Equal(["resume"], Tokenize(analyzer, "résumé"));
    }

    [Fact]
    public void EnglishAnalyzer_Stems()
    {
        var analyzer = AnalyzerHelper.TryCreatePredefined("en.lucene")!;

        // The point is that inflected forms collapse to one term, not what that term is spelled
        // as — the stemmer's output is Lucene's business.
        var running = Tokenize(analyzer, "running");
        var runs = Tokenize(analyzer, "runs");

        Assert.Equal(running, runs);
    }

    [Fact]
    public void CustomAnalyzer_AppliesItsWholeChain()
    {
        var index = CreateIndex("""
            "analyzers": [
              {
                "name": "myAnalyzer",
                "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                "tokenizer": "standard_v2",
                "tokenFilters": ["lowercase", "asciifolding"],
                "charFilters": ["html_strip"]
              }
            ]
            """);

        var analyzer = AnalyzerHelper.GetAnalyzer(index, "myAnalyzer");

        // html_strip removes the markup, standard splits, lowercase and asciifolding normalize.
        Assert.Equal(["cafe", "noir"], Tokenize(analyzer, "<b>CAFÉ</b> Noir"));
    }

    /// <summary>
    /// Token filters apply in the order declared, so a chain has to preserve it.
    /// </summary>
    [Fact]
    public void CustomAnalyzer_AppliesTokenFiltersInOrder()
    {
        var index = CreateIndex("""
            "analyzers": [
              {
                "name": "clipped",
                "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                "tokenizer": "whitespace",
                "tokenFilters": ["lowercase", "truncateToThree"]
              }
            ],
            "tokenFilters": [
              {
                "name": "truncateToThree",
                "@odata.type": "#Microsoft.Azure.Search.TruncateTokenFilter",
                "length": 3
              }
            ]
            """);

        var analyzer = AnalyzerHelper.GetAnalyzer(index, "clipped");

        Assert.Equal(["abc", "def"], Tokenize(analyzer, "ABCDEF DEFGHI"));
    }

    /// <summary>
    /// A component definition supplies options to a built-in; without them the factory would
    /// use its own defaults and the definition would have no effect.
    /// </summary>
    [Fact]
    public void DefinedComponent_SuppliesItsOptions()
    {
        var index = CreateIndex("""
            "analyzers": [
              {
                "name": "grams",
                "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                "tokenizer": "gram3"
              }
            ],
            "tokenizers": [
              {
                "name": "gram3",
                "@odata.type": "#Microsoft.Azure.Search.NGramTokenizer",
                "minGram": 3,
                "maxGram": 3
              }
            ]
            """);

        var analyzer = AnalyzerHelper.GetAnalyzer(index, "grams");

        Assert.Equal(["abc", "bcd", "cde"], Tokenize(analyzer, "abcde"));
    }

    /// <summary>
    /// A char filter with options — mappings are the case that would silently do nothing if the
    /// options were not passed to the factory.
    /// </summary>
    [Fact]
    public void MappingCharFilter_AppliesItsMappings()
    {
        var index = CreateIndex("""
            "analyzers": [
              {
                "name": "mapped",
                "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                "tokenizer": "whitespace",
                "charFilters": ["dashToSpace"]
              }
            ],
            "charFilters": [
              {
                "name": "dashToSpace",
                "@odata.type": "#Microsoft.Azure.Search.MappingCharFilter",
                "mappings": ["-=>_"]
              }
            ]
            """);

        var analyzer = AnalyzerHelper.GetAnalyzer(index, "mapped");

        Assert.Equal(["a_b"], Tokenize(analyzer, "a-b"));
    }

    /// <summary>
    /// Azure gives a stopword list inline; Lucene's stop filter reads one from a resource and
    /// has to be informed of it after construction. A filter built but never informed leaves
    /// its word set null and throws from inside the token stream on the first document.
    /// </summary>
    [Fact]
    public void StopwordsTokenFilter_RemovesItsInlineWords()
    {
        var index = CreateIndex("""
            "analyzers": [
              {
                "name": "filtered",
                "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                "tokenizer": "whitespace",
                "tokenFilters": ["myStopwords"]
              }
            ],
            "tokenFilters": [
              {
                "name": "myStopwords",
                "@odata.type": "#Microsoft.Azure.Search.StopwordsTokenFilter",
                "stopwords": ["the", "and"]
              }
            ]
            """);

        var analyzer = AnalyzerHelper.GetAnalyzer(index, "filtered");

        Assert.Equal(["quick", "fox"], Tokenize(analyzer, "the quick and fox"));
    }

    /// <summary>
    /// The n-gram bounds are spelled differently by Azure and Lucene, and a mismatch is silent:
    /// the factory rejects the unknown option rather than falling back, so the whole analyzer
    /// fails to build.
    /// </summary>
    [Fact]
    public void EdgeNGramTokenFilter_UsesAzuresOptionNames()
    {
        var index = CreateIndex("""
            "analyzers": [
              {
                "name": "edges",
                "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                "tokenizer": "whitespace",
                "tokenFilters": ["myEdges"]
              }
            ],
            "tokenFilters": [
              {
                "name": "myEdges",
                "@odata.type": "#Microsoft.Azure.Search.EdgeNGramTokenFilterV2",
                "minGram": 2,
                "maxGram": 3
              }
            ]
            """);

        var analyzer = AnalyzerHelper.GetAnalyzer(index, "edges");

        Assert.Equal(["ab", "abc"], Tokenize(analyzer, "abcd"));
    }

    [Fact]
    public void PatternAnalyzerDefinition_SplitsOnItsPattern()
    {
        var index = CreateIndex("""
            "analyzers": [
              {
                "name": "commas",
                "@odata.type": "#Microsoft.Azure.Search.PatternAnalyzer",
                "pattern": ",",
                "lowercase": true
              }
            ]
            """);

        var analyzer = AnalyzerHelper.GetAnalyzer(index, "commas");

        Assert.Equal(["a b", "c"], Tokenize(analyzer, "A B,C"));
    }

    [Fact]
    public void StopAnalyzerDefinition_RemovesItsStopwords()
    {
        var index = CreateIndex("""
            "analyzers": [
              {
                "name": "noThe",
                "@odata.type": "#Microsoft.Azure.Search.StopAnalyzer",
                "stopwords": ["the"]
              }
            ]
            """);

        var analyzer = AnalyzerHelper.GetAnalyzer(index, "noThe");

        Assert.Equal(["quick", "fox"], Tokenize(analyzer, "the quick the fox"));
    }

    /// <summary>
    /// Azure's standard analyzer defaults to no stopwords, unlike Lucene's ctor default of the
    /// English set. Getting this wrong would silently drop common words from an index that
    /// asked for none to be dropped.
    /// </summary>
    [Fact]
    public void StandardAnalyzerDefinition_KeepsWordsWhenNoStopwordsDeclared()
    {
        var index = CreateIndex("""
            "analyzers": [
              {
                "name": "plain",
                "@odata.type": "#Microsoft.Azure.Search.StandardAnalyzer"
              }
            ]
            """);

        var analyzer = AnalyzerHelper.GetAnalyzer(index, "plain");

        Assert.Equal(["the", "quick", "fox"], Tokenize(analyzer, "the quick fox"));
    }

    /// <summary>
    /// A definition the emulator does not recognize is still one a client may hold; it must
    /// survive a get-modify-put rather than being rewritten into something else.
    /// </summary>
    [Fact]
    public void UnrecognizedAnalyzerType_RoundTripsWithItsDiscriminator()
    {
        var index = CreateIndex("""
            "analyzers": [
              {
                "name": "exotic",
                "@odata.type": "#Microsoft.Azure.Search.SomeFutureAnalyzer",
                "someOption": 42
              }
            ]
            """);

        var json = JsonSerializer.Serialize(index, Options);
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        var analyzer = result.GetProperty("analyzers")[0];

        Assert.Equal("#Microsoft.Azure.Search.SomeFutureAnalyzer",
            analyzer.GetProperty("@odata.type").GetString());
        Assert.Equal(42, analyzer.GetProperty("someOption").GetInt32());
    }

    [Fact]
    public void CustomAnalyzer_RoundTripsItsChain()
    {
        var index = CreateIndex("""
            "analyzers": [
              {
                "name": "myAnalyzer",
                "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                "tokenizer": "standard_v2",
                "tokenFilters": ["lowercase"],
                "charFilters": ["html_strip"]
              }
            ]
            """);

        var json = JsonSerializer.Serialize(index, Options);
        var result = JsonSerializer.Deserialize<SearchIndex>(json, Options)!;

        var analyzer = Assert.IsType<CustomAnalyzer>(result.FindAnalyzer("myAnalyzer"));

        Assert.Equal("standard_v2", analyzer.Tokenizer);
        Assert.Equal(["lowercase"], analyzer.TokenFilters);
        Assert.Equal(["html_strip"], analyzer.CharFilters);
    }

    /// <summary>
    /// The search-side analyzer has to resolve the index's custom definitions too. If only the
    /// index side did, a document would be tokenized one way and the query another, and the
    /// search would find nothing.
    /// </summary>
    [Fact]
    public void PerFieldSearchAnalyzer_UsesTheIndexsOwnDefinition()
    {
        var json = """
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "text", "type": "Edm.String", "searchable": true, "analyzer": "upper" }
              ],
              "analyzers": [
                {
                  "name": "upper",
                  "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                  "tokenizer": "whitespace",
                  "tokenFilters": ["uppercase"]
                }
              ]
            }
            """;

        var index = JsonSerializer.Deserialize<SearchIndex>(json, Options)!;

        var analyzer = AnalyzerHelper.GetPerFieldSearchAnalyzer(index);

        Assert.Equal(["HELLO"], Tokenize(analyzer, "hello", "text"));
    }

    /// <summary>
    /// A field's analyzer resolves against the index's definitions, which is the whole point of
    /// defining one.
    /// </summary>
    [Fact]
    public void PerFieldAnalyzer_UsesTheIndexsOwnDefinition()
    {
        var json = """
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "text", "type": "Edm.String", "searchable": true, "analyzer": "upper" }
              ],
              "analyzers": [
                {
                  "name": "upper",
                  "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                  "tokenizer": "whitespace",
                  "tokenFilters": ["uppercase"]
                }
              ]
            }
            """;

        var index = JsonSerializer.Deserialize<SearchIndex>(json, Options)!;

        var analyzer = AnalyzerHelper.GetPerFieldIndexAnalyzer(index);

        Assert.Equal(["HELLO"], Tokenize(analyzer, "hello", "text"));

        // A field that names no analyzer stays on the default, which does not uppercase.
        Assert.Equal(["hello"], Tokenize(analyzer, "hello", "id"));
    }
}

/// <summary>
/// Unit tests for definition-time validation of analyzers (issue #34).
/// </summary>
/// <remarks>
/// As in <see cref="VectorSearchValidationTests"/>, the assertions check that a message names
/// the thing at fault rather than matching it verbatim.
/// </remarks>
public class AnalyzerValidationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ValidIndex_PassesValidation()
    {
        var index = JsonSerializer.Deserialize<SearchIndex>("""
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "text", "type": "Edm.String", "searchable": true, "analyzer": "en.microsoft" }
              ]
            }
            """, Options)!;

        Assert.Null(AnalyzerValidator.FindInvalidAnalyzer(index));
    }

    /// <summary>
    /// The failure the issue is about: an unknown analyzer was accepted at index creation and
    /// only surfaced later, when a document was indexed.
    /// </summary>
    [Theory]
    [InlineData("analyzer")]
    [InlineData("indexAnalyzer")]
    [InlineData("searchAnalyzer")]
    public void FieldNamingUnknownAnalyzer_IsRejected(string property)
    {
        var index = JsonSerializer.Deserialize<SearchIndex>($$"""
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "text", "type": "Edm.String", "searchable": true, "{{property}}": "no.such.analyzer" }
              ]
            }
            """, Options)!;

        var error = AnalyzerValidator.FindInvalidAnalyzer(index);

        Assert.NotNull(error);
        Assert.Contains("no.such.analyzer", error);
        Assert.Contains("text", error);
        Assert.Contains(property, error);
    }

    /// <summary>
    /// A mistake on a sub-field should be reported against its path, not swallowed because the
    /// walk stopped at the top level.
    /// </summary>
    [Fact]
    public void SubFieldNamingUnknownAnalyzer_IsRejectedByPath()
    {
        var index = JsonSerializer.Deserialize<SearchIndex>("""
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                {
                  "name": "author",
                  "type": "Edm.ComplexType",
                  "fields": [
                    { "name": "bio", "type": "Edm.String", "searchable": true, "analyzer": "bogus" }
                  ]
                }
              ]
            }
            """, Options)!;

        var error = AnalyzerValidator.FindInvalidAnalyzer(index);

        Assert.NotNull(error);
        Assert.Contains("author/bio", error);
    }

    [Fact]
    public void FieldNamingDefinedAnalyzer_IsAccepted()
    {
        var index = JsonSerializer.Deserialize<SearchIndex>("""
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "text", "type": "Edm.String", "searchable": true, "analyzer": "mine" }
              ],
              "analyzers": [
                {
                  "name": "mine",
                  "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                  "tokenizer": "standard_v2"
                }
              ]
            }
            """, Options)!;

        Assert.Null(AnalyzerValidator.FindInvalidAnalyzer(index));
    }

    [Fact]
    public void CustomAnalyzerNamingUnknownTokenizer_IsRejected()
    {
        var index = JsonSerializer.Deserialize<SearchIndex>("""
            {
              "name": "test",
              "fields": [{ "name": "id", "type": "Edm.String", "key": true }],
              "analyzers": [
                {
                  "name": "mine",
                  "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                  "tokenizer": "no_such_tokenizer"
                }
              ]
            }
            """, Options)!;

        var error = AnalyzerValidator.FindInvalidAnalyzer(index);

        Assert.NotNull(error);
        Assert.Contains("no_such_tokenizer", error);
    }

    [Fact]
    public void CustomAnalyzerWithoutTokenizer_IsRejected()
    {
        var index = JsonSerializer.Deserialize<SearchIndex>("""
            {
              "name": "test",
              "fields": [{ "name": "id", "type": "Edm.String", "key": true }],
              "analyzers": [
                { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer" }
              ]
            }
            """, Options)!;

        var error = AnalyzerValidator.FindInvalidAnalyzer(index);

        Assert.NotNull(error);
        Assert.Contains("tokenizer", error);
    }

    /// <summary>
    /// Azure refuses a custom analyzer that takes a built-in's name; allowing it would leave the
    /// definition unreachable behind whichever lookup won.
    /// </summary>
    [Fact]
    public void CustomAnalyzerTakingPredefinedName_IsRejected()
    {
        var index = JsonSerializer.Deserialize<SearchIndex>("""
            {
              "name": "test",
              "fields": [{ "name": "id", "type": "Edm.String", "key": true }],
              "analyzers": [
                {
                  "name": "standard",
                  "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                  "tokenizer": "standard_v2"
                }
              ]
            }
            """, Options)!;

        var error = AnalyzerValidator.FindInvalidAnalyzer(index);

        Assert.NotNull(error);
        Assert.Contains("standard", error);
    }

    [Fact]
    public void DuplicateAnalyzerNames_AreRejected()
    {
        var index = JsonSerializer.Deserialize<SearchIndex>("""
            {
              "name": "test",
              "fields": [{ "name": "id", "type": "Edm.String", "key": true }],
              "analyzers": [
                { "name": "mine", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer", "tokenizer": "whitespace" },
                { "name": "MINE", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer", "tokenizer": "whitespace" }
              ]
            }
            """, Options)!;

        var error = AnalyzerValidator.FindInvalidAnalyzer(index);

        Assert.NotNull(error);
        Assert.Contains("more than one", error);
    }

    /// <summary>
    /// The proprietary Microsoft tokenizers have no Lucene equivalent, so a chain naming one
    /// cannot be built. Saying so is more useful than reporting it as an unknown name.
    /// </summary>
    [Fact]
    public void UnsupportedComponent_IsRejectedWithAReason()
    {
        var index = JsonSerializer.Deserialize<SearchIndex>("""
            {
              "name": "test",
              "fields": [{ "name": "id", "type": "Edm.String", "key": true }],
              "analyzers": [
                {
                  "name": "mine",
                  "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
                  "tokenizer": "microsoft_language_tokenizer"
                }
              ]
            }
            """, Options)!;

        var error = AnalyzerValidator.FindInvalidAnalyzer(index);

        Assert.NotNull(error);
        Assert.Contains("not supported", error);
    }
}

