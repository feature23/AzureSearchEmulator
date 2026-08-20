using AzureSearchEmulator.SearchData;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Util;
using Xunit;
using SynonymMap = AzureSearchEmulator.Models.SynonymMap;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Exercises synonym expansion in isolation, before any index or query is involved (issue #69).
/// </summary>
/// <remarks>
/// The two Solr rule forms mean different things, and the difference is the part of this
/// feature most easily got wrong: an equivalency rule widens a query while keeping the term the
/// caller typed, and a mapping rule replaces it. Asserting on the token stream shows which of
/// the two a rule produced, which a search result alone would not — a query that matched could
/// have matched on the original term.
///
/// See <see cref="SynonymMapEndToEndTests"/> for the same rules driving a real search.
/// </remarks>
public class SynonymMapTests
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    private static SynonymMap CreateMap(string synonyms) =>
        new() { Name = "test", Synonyms = synonyms };

    /// <summary>
    /// Expands text through a lower-casing chain with the map applied, the way a searchable
    /// field's analyzer does.
    /// </summary>
    private static List<string> Expand(string synonyms, string text)
    {
        var analyzer = SynonymMapHelper.Wrap(CreateBaseAnalyzer(), [CreateMap(synonyms)]);

        using var stream = analyzer.GetTokenStream("field", text);
        var term = stream.AddAttribute<ICharTermAttribute>();
        var tokens = new List<string>();

        stream.Reset();

        while (stream.IncrementToken())
        {
            tokens.Add(term.ToString());
        }

        stream.End();

        return tokens;
    }

    private static Analyzer CreateBaseAnalyzer() =>
        Analyzer.NewAnonymous((_, reader) =>
        {
            Tokenizer source = new StandardTokenizer(Version, reader);

            return new TokenStreamComponents(source, new LowerCaseFilter(Version, source));
        });

    /// <summary>
    /// An equivalency rule adds every alternative and keeps the term that was typed, so a
    /// document holding any one of them matches a query using any other.
    /// </summary>
    [Theory]
    [InlineData("usa")]
    [InlineData("united states")]
    public void EquivalencyRule_ExpandsInEveryDirection(string query)
    {
        var tokens = Expand("usa, united states", query);

        Assert.Contains("usa", tokens);
        Assert.Contains("united", tokens);
        Assert.Contains("states", tokens);
    }

    /// <summary>
    /// A mapping rule replaces its left side, so the original term is gone from the query.
    /// </summary>
    /// <remarks>
    /// This is the assertion that separates the two rule forms. Were the arrow treated as an
    /// equivalency, "dog" would survive alongside "canine" and a search for it would still
    /// match documents about dogs — which is the opposite of what the rule asks for.
    /// </remarks>
    [Fact]
    public void MappingRule_ReplacesTheTermItMatches()
    {
        var tokens = Expand("dog => canine", "dog");

        Assert.Equal(["canine"], tokens);
    }

    /// <summary>
    /// A mapping rule may name several replacements, and all of them are produced.
    /// </summary>
    [Fact]
    public void MappingRule_ProducesEveryReplacement()
    {
        var tokens = Expand("dog => canine, hound", "dog");

        Assert.Contains("canine", tokens);
        Assert.Contains("hound", tokens);
        Assert.DoesNotContain("dog", tokens);
    }

    /// <summary>
    /// A term no rule mentions passes through untouched.
    /// </summary>
    [Fact]
    public void UnmatchedTerm_IsLeftAlone()
    {
        Assert.Equal(["cat"], Expand("dog => canine", "cat"));
    }

    /// <summary>
    /// Matching ignores case, because the rules are lower-cased when parsed and the field's own
    /// analyzer lower-cases the query before the filter sees it.
    /// </summary>
    /// <remarks>
    /// Covers the case that would otherwise fail silently: a map written in lower case and a
    /// query typed in capitals is the ordinary way both are used, and an expansion that only
    /// worked when the two happened to agree would look correct in every hand-written test.
    /// </remarks>
    [Theory]
    [InlineData("USA")]
    [InlineData("Usa")]
    public void Matching_IgnoresCase(string query)
    {
        Assert.Contains("united", Expand("usa, united states", query));
    }

    /// <summary>
    /// A multi-word rule matches the words in sequence rather than individually.
    /// </summary>
    [Fact]
    public void MultiWordRule_MatchesThePhrase()
    {
        var synonyms = "united states of america, usa";

        Assert.Contains("usa", Expand(synonyms, "united states of america"));

        // The leading word on its own is not the phrase, so it must not trigger the rule.
        Assert.DoesNotContain("usa", Expand(synonyms, "united"));
    }

    /// <summary>
    /// Rules are separated by newlines, and every one of them applies.
    /// </summary>
    [Fact]
    public void SeveralRules_AllApply()
    {
        var synonyms = "usa, united states\ndog => canine";

        Assert.Contains("united", Expand(synonyms, "usa"));
        Assert.Equal(["canine"], Expand(synonyms, "dog"));
    }

    /// <summary>
    /// A field naming several maps gets all of them, each as its own filter.
    /// </summary>
    [Fact]
    public void SeveralMaps_AreAllApplied()
    {
        var analyzer = SynonymMapHelper.Wrap(
            CreateBaseAnalyzer(),
            [CreateMap("usa, united states"), CreateMap("dog => canine")]);

        Assert.Contains("united", Tokens(analyzer, "usa"));
        Assert.Equal(["canine"], Tokens(analyzer, "dog"));
    }

    /// <summary>
    /// Wrapping with no maps returns the analyzer untouched, so a field that names none pays
    /// nothing for the feature.
    /// </summary>
    [Fact]
    public void NoMaps_LeavesTheAnalyzerAlone()
    {
        var analyzer = CreateBaseAnalyzer();

        Assert.Same(analyzer, SynonymMapHelper.Wrap(analyzer, []));
    }

    /// <summary>
    /// A compiled map is reused while its definition is, so the rules are not re-parsed on
    /// every search.
    /// </summary>
    [Fact]
    public void CompiledMap_IsCachedPerDefinition()
    {
        var map = CreateMap("usa, united states");

        Assert.Same(SynonymMapHelper.Compile(map), SynonymMapHelper.Compile(map));
    }

    /// <summary>
    /// An edited map is a new definition, so it compiles afresh rather than answering from the
    /// rules it replaced.
    /// </summary>
    [Fact]
    public void EditedMap_IsNotServedFromTheCache()
    {
        Assert.NotSame(
            SynonymMapHelper.Compile(CreateMap("usa, united states")),
            SynonymMapHelper.Compile(CreateMap("usa, us")));
    }

    /// <summary>
    /// Only Azure's one supported format is accepted; anything else would be stored and then
    /// expand nothing.
    /// </summary>
    [Fact]
    public void UnsupportedFormat_IsRejected()
    {
        var map = new SynonymMap { Name = "test", Format = "wordnet", Synonyms = "usa, us" };

        var ex = Assert.Throws<SynonymMapDefinitionException>(() => SynonymMapBuilder.Build(map));

        Assert.Contains("wordnet", ex.Message);
    }

    private static List<string> Tokens(Analyzer analyzer, string text)
    {
        using var stream = analyzer.GetTokenStream("field", text);
        var term = stream.AddAttribute<ICharTermAttribute>();
        var tokens = new List<string>();

        stream.Reset();

        while (stream.IncrementToken())
        {
            tokens.Add(term.ToString());
        }

        stream.End();

        return tokens;
    }
}
