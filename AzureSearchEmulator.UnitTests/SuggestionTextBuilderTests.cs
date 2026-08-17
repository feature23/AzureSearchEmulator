using AzureSearchEmulator.Searching;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for the text handling behind suggest and autocomplete (issue #45), covering the
/// tokenizing, highlighting and completion rules on their own rather than through a Lucene
/// index.
/// </summary>
public class SuggestionTextBuilderTests
{
    [Theory]
    [InlineData("laptop", new[] { "laptop" })]
    [InlineData("Laptop Pro", new[] { "laptop", "pro" })]
    [InlineData("  spaced   out  ", new[] { "spaced", "out" })]
    // Punctuation is a boundary because the analyzer strips it at index time.
    [InlineData("hello, world!", new[] { "hello", "world" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    public void Tokenize_SplitsAndLowercases(string input, string[] expected)
    {
        Assert.Equal(expected, SuggesterSupport.Tokenize(input));
    }

    [Fact]
    public void ApplyHighlighting_WrapsTheTypedPrefixOfThePartialTerm()
    {
        var result = SuggestionTextBuilder.ApplyHighlighting(
            "Laptop Pro 15", ["lap"], "<b>", "</b>");

        Assert.Equal("<b>Lap</b>top Pro 15", result);
    }

    [Fact]
    public void ApplyHighlighting_WrapsCompletedTermsWhole()
    {
        var result = SuggestionTextBuilder.ApplyHighlighting(
            "Laptop Pro 15", ["laptop", "pr"], "<b>", "</b>");

        Assert.Equal("<b>Laptop</b> <b>Pr</b>o 15", result);
    }

    [Fact]
    public void ApplyHighlighting_WithoutTags_ReturnsTheTextUnchanged()
    {
        var result = SuggestionTextBuilder.ApplyHighlighting("Laptop Pro 15", ["lap"], null, null);

        Assert.Equal("Laptop Pro 15", result);
    }

    [Fact]
    public void ApplyHighlighting_PreservesPunctuationAndSpacing()
    {
        var result = SuggestionTextBuilder.ApplyHighlighting(
            "Fast, cheap laptops!", ["laptop"], "<b>", "</b>");

        Assert.Equal("Fast, cheap <b>laptop</b>s!", result);
    }

    [Fact]
    public void GetCompletions_OneTerm_ReturnsTheWordThePrefixLandedIn()
    {
        var result = SuggestionTextBuilder.GetCompletions(
            "High-performance laptop computer", "lap", AutocompleteModes.OneTerm);

        Assert.Equal(["laptop"], result);
    }

    [Fact]
    public void GetCompletions_TwoTerms_AppendsTheFollowingWord()
    {
        var result = SuggestionTextBuilder.GetCompletions(
            "High-performance laptop computer", "lap", AutocompleteModes.TwoTerms);

        Assert.Equal(["laptop computer"], result);
    }

    [Fact]
    public void GetCompletions_TwoTerms_FallsBackToOneWordAtTheEndOfTheText()
    {
        var result = SuggestionTextBuilder.GetCompletions(
            "a fast laptop", "lap", AutocompleteModes.TwoTerms);

        Assert.Equal(["laptop"], result);
    }

    [Fact]
    public void GetContextualCompletions_RequiresThePrecedingWordsToMatch()
    {
        Assert.Equal(
            ["laptop"],
            SuggestionTextBuilder.GetContextualCompletions("a fast laptop", ["fast"], "lap"));

        Assert.Empty(
            SuggestionTextBuilder.GetContextualCompletions("a fast laptop", ["cheap"], "lap"));
    }

    [Fact]
    public void GetContextualCompletions_WithNoContext_BehavesLikeOneTerm()
    {
        Assert.Equal(
            ["laptop"],
            SuggestionTextBuilder.GetContextualCompletions("a fast laptop", [], "lap"));
    }

    [Fact]
    public void GetSuggestionText_PicksTheFirstSourceFieldThatContainsTheTerms()
    {
        var text = SuggestionTextBuilder.GetSuggestionText(
            ["Name", "Description"],
            field => field switch
            {
                "Name" => "Gaming Mouse",
                "Description" => "Precision gaming mouse",
                _ => null,
            },
            ["precisio"]);

        Assert.Equal("Precision gaming mouse", text);
    }

    [Fact]
    public void GetSuggestionText_ReturnsNullWhenNoSourceFieldContainsTheTerms()
    {
        var text = SuggestionTextBuilder.GetSuggestionText(
            ["Name"],
            _ => "Gaming Mouse",
            ["laptop"]);

        Assert.Null(text);
    }

    [Fact]
    public void GetSuggestionText_Fuzzy_FallsBackToTheFirstPopulatedField()
    {
        // A misspelled term appears nowhere literally, but the query matched the document, so
        // the suggestion still has to carry text.
        var text = SuggestionTextBuilder.GetSuggestionText(
            ["Name", "Description"],
            field => field == "Name" ? "" : "Affordable laptop",
            ["laptob"],
            fuzzy: true);

        Assert.Equal("Affordable laptop", text);
    }

    [Fact]
    public void GetSuggestionText_Fuzzy_StillPrefersTheFieldThatLiterallyMatches()
    {
        var text = SuggestionTextBuilder.GetSuggestionText(
            ["Name", "Description"],
            field => field == "Name" ? "Gaming Mouse" : "Affordable laptop",
            ["laptop"],
            fuzzy: true);

        Assert.Equal("Affordable laptop", text);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void ValidateTop_AcceptsOneThroughOneHundred(int top, bool valid)
    {
        Assert.Equal(valid, SuggesterSupport.ValidateTop(top) == null);
    }

    [Theory]
    [InlineData("oneTerm", true)]
    [InlineData("twoTerms", true)]
    [InlineData("oneTermWithContext", true)]
    [InlineData("ONETERM", true)]
    [InlineData("bogus", false)]
    [InlineData(null, false)]
    public void AutocompleteModes_IsValid_AcceptsOnlyTheDocumentedModes(string? mode, bool valid)
    {
        Assert.Equal(valid, AutocompleteModes.IsValid(mode));
    }
}
