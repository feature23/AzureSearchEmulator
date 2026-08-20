using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureSearchEmulator.Models;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Pattern;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Util;

namespace AzureSearchEmulator.SearchData;

/// <summary>
/// Builds a Lucene analyzer from the definitions an index carries (issue #34).
/// </summary>
/// <remarks>
/// Azure's custom analyzers and Lucene's analysis SPI describe the same thing in the same
/// order: optional char filters rewrite the raw text, one tokenizer splits it, then token
/// filters transform the stream. Azure's built-in component set is itself drawn from Lucene, so
/// the emulator does not reimplement any of it — it maps Azure's name for a component onto the
/// Lucene factory's SPI name and lets the factory parse its own options.
///
/// That mapping is the whole of the work here, and it is needed because the two spell the same
/// component differently: Azure writes <c>keyword_v2</c>, <c>edgeNGram</c> and
/// <c>pattern_replace</c> where Lucene's SPI registers <c>keyword</c>, <c>edgengram</c> and
/// <c>patternreplace</c>. The <c>_v2</c> suffixes are Azure's own versioning of components
/// whose behaviour changed, not a distinct Lucene implementation, so both spellings resolve to
/// the same factory.
/// </remarks>
public static class CustomAnalyzerBuilder
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    /// <summary>
    /// The argument every Lucene analysis factory reads to decide its version-dependent
    /// behaviour.
    /// </summary>
    private const string MatchVersionArg = "luceneMatchVersion";

    private static readonly string MatchVersionValue = Version.ToString();

    /// <summary>
    /// Azure tokenizer names that differ from the Lucene SPI name for the same tokenizer.
    /// </summary>
    /// <remarks>
    /// Names not listed here are passed through unchanged after lowercasing, which covers
    /// <c>classic</c>, <c>letter</c>, <c>lowercase</c>, <c>pattern</c> and <c>whitespace</c>.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> TokenizerNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["standard_v2"] = "standard",
            ["keyword_v2"] = "keyword",
            ["path_hierarchy_v2"] = "pathhierarchy",
            ["edgeNGram"] = "edgengram",
            ["nGram"] = "ngram",
            ["uax_url_email"] = "uax29urlemail",
        };

    /// <summary>
    /// Azure token filter names that differ from the Lucene SPI name for the same filter.
    /// </summary>
    /// <remarks>
    /// Azure separates words with underscores where Lucene's SPI runs them together, so most of
    /// these are that one transformation; they are listed rather than derived because a handful
    /// (<c>stopwords</c>, <c>limit</c>, <c>snowball</c>, <c>unique</c>) are genuinely different
    /// words from the Lucene name, and a rule that got those wrong would silently build the
    /// wrong chain.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> TokenFilterNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["arabic_normalization"] = "arabicnormalization",
            ["cjk_bigram"] = "cjkbigram",
            ["cjk_width"] = "cjkwidth",
            ["common_grams"] = "commongrams",
            ["dictionary_decompounder"] = "dictionarycompoundword",
            ["edgeNGram_v2"] = "edgengram",
            ["edgeNGram"] = "edgengram",
            ["german_normalization"] = "germannormalization",
            ["hindi_normalization"] = "hindinormalization",
            ["indic_normalization"] = "indicnormalization",
            ["keep"] = "keepword",
            ["keyword_marker"] = "keywordmarker",
            ["keyword_repeat"] = "keywordrepeat",
            ["limit"] = "limittokencount",
            ["nGram_v2"] = "ngram",
            ["nGram"] = "ngram",
            ["pattern_capture"] = "patterncapturegroup",
            ["pattern_replace"] = "patternreplace",
            ["persian_normalization"] = "persiannormalization",
            ["porter_stem"] = "porterstem",
            ["reverse"] = "reversestring",
            ["scandinavian_folding"] = "scandinavianfolding",
            ["scandinavian_normalization"] = "scandinaviannormalization",
            ["snowball"] = "snowballporter",
            ["sorani_normalization"] = "soraninormalization",
            ["stemmer"] = "snowballporter",
            ["stemmer_override"] = "stemmeroverride",
            ["stopwords"] = "stop",
            ["unique"] = "removeduplicates",
            ["word_delimiter"] = "worddelimiter",
        };

    /// <summary>
    /// Azure char filter names that differ from the Lucene SPI name.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> CharFilterNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["html_strip"] = "htmlstrip",
            ["pattern_replace"] = "patternreplace",
        };

    /// <summary>
    /// Components the emulator has no Lucene implementation for.
    /// </summary>
    /// <remarks>
    /// The <c>microsoft_language_*</c> tokenizers are the proprietary NLP stack, and
    /// <c>phonetic</c> lives in a Lucene contrib package this project does not reference.
    /// Naming them explicitly lets the validator say what is wrong, rather than reporting them
    /// the same way as a typo.
    /// </remarks>
    public static readonly IReadOnlySet<string> UnsupportedComponents =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft_language_tokenizer",
            "microsoft_language_stemming_tokenizer",
            "phonetic",
        };

    /// <summary>
    /// Builds the analyzer an index defines under <paramref name="name"/>, or null when it
    /// defines none.
    /// </summary>
    public static Analyzer? TryBuild(SearchIndex index, string name)
        => index.FindAnalyzer(name) is { } definition ? Build(index, definition) : null;

    /// <summary>
    /// Builds one analyzer definition into a Lucene analyzer.
    /// </summary>
    /// <exception cref="AnalyzerDefinitionException">
    /// The definition names a component the emulator cannot build, or one of its options is not
    /// one the underlying factory accepts.
    /// </exception>
    public static Analyzer Build(SearchIndex index, LexicalAnalyzerDefinition definition)
    {
        return definition switch
        {
            PatternAnalyzerDefinition pattern => BuildPattern(pattern),
            StandardAnalyzerDefinition standard => BuildStandard(standard),
            StopAnalyzerDefinition stop => new StopAnalyzer(Version, ToStopwordSet(stop.Stopwords)),
            CustomAnalyzer custom => BuildCustom(index, custom),
            _ => throw new AnalyzerDefinitionException(
                $"Analyzer '{definition.Name}' has type '{definition.ODataType}', which is not supported.")
        };
    }

    private static Analyzer BuildPattern(PatternAnalyzerDefinition definition)
    {
        Regex regex;

        try
        {
            regex = new Regex(definition.Pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            throw new AnalyzerDefinitionException(
                $"Analyzer '{definition.Name}' has pattern '{definition.Pattern}', which is not " +
                $"a valid regular expression: {ex.Message}");
        }

        return CreatePatternAnalyzer(regex, definition.LowerCase, definition.Stopwords);
    }

    /// <summary>
    /// Builds Azure's pattern analyzer: split on a regular expression, optionally lowercase,
    /// then drop stopwords.
    /// </summary>
    /// <remarks>
    /// Assembled from the individual components rather than Lucene's <c>PatternAnalyzer</c>,
    /// which is deprecated in 4.8. <see cref="PatternTokenizer"/> with a group of -1 treats the
    /// expression as a separator, which is the behaviour Azure documents.
    /// </remarks>
    /// <param name="stopwords">
    /// Null or empty leaves the stream unfiltered. Azure's pattern analyzer defaults to no
    /// stopwords, unlike several Lucene analyzers that default to the English set.
    /// </param>
    public static Analyzer CreatePatternAnalyzer(Regex pattern, bool lowerCase, IList<string>? stopwords)
    {
        var stopwordSet = stopwords is { Count: > 0 } ? ToStopwordSet(stopwords) : null;

        return Analyzer.NewAnonymous((_, reader) =>
        {
            var tokenizer = new PatternTokenizer(reader, pattern, -1);
            TokenStream stream = tokenizer;

            if (lowerCase)
            {
                stream = new LowerCaseFilter(Version, stream);
            }

            if (stopwordSet != null)
            {
                stream = new StopFilter(Version, stream, stopwordSet);
            }

            return new TokenStreamComponents(tokenizer, stream);
        });
    }

    private static Analyzer BuildStandard(StandardAnalyzerDefinition definition)
    {
        if (definition.MaxTokenLength is < 1 or > AnalysisComponentJson.MaxTokenLengthLimit)
        {
            throw new AnalyzerDefinitionException(
                $"Analyzer '{definition.Name}' declares a maxTokenLength of " +
                $"{definition.MaxTokenLength}, which must be between 1 and " +
                $"{AnalysisComponentJson.MaxTokenLengthLimit}.");
        }

        // Azure's StandardAnalyzer takes an explicit stopword list and defaults to none, unlike
        // Lucene's ctor default of the English set. Passing the list through as given keeps an
        // index that declares no stopwords from quietly acquiring English ones.
        var analyzer = new StandardAnalyzer(Version, ToStopwordSet(definition.Stopwords))
        {
            MaxTokenLength = definition.MaxTokenLength
        };

        return analyzer;
    }

    private static Analyzer BuildCustom(SearchIndex index, CustomAnalyzer definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Tokenizer))
        {
            throw new AnalyzerDefinitionException(
                $"Custom analyzer '{definition.Name}' must name a tokenizer.");
        }

        var tokenizerFactory = CreateTokenizerFactory(index, definition);

        var tokenFilterFactories = definition.TokenFilters
            .Select(i => CreateTokenFilterFactory(index, definition, i))
            .ToList();

        var charFilterFactories = definition.CharFilters
            .Select(i => CreateCharFilterFactory(index, definition, i))
            .ToList();

        return Analyzer.NewAnonymous(
            createComponents: (_, reader) =>
            {
                var tokenizer = tokenizerFactory.Create(reader);

                // Each filter wraps the stream the previous one produced, so the declared order
                // is the order they apply in.
                TokenStream stream = tokenizer;

                foreach (var factory in tokenFilterFactories)
                {
                    stream = factory.Create(stream);
                }

                return new TokenStreamComponents(tokenizer, stream);
            },
            initReader: (_, reader) =>
            {
                foreach (var factory in charFilterFactories)
                {
                    reader = factory.Create(reader);
                }

                return reader;
            });
    }

    private static TokenizerFactory CreateTokenizerFactory(SearchIndex index, CustomAnalyzer analyzer)
    {
        var resolved = ResolveComponent(
            index.Tokenizers, analyzer, analyzer.Tokenizer, TokenizerNames, "tokenizer");

        try
        {
            var factory = TokenizerFactory.ForName(resolved.SpiName, resolved.Args);
            Inform(factory, resolved);
            return factory;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
        {
            throw ComponentFailure(analyzer, analyzer.Tokenizer, "tokenizer", resolved, ex);
        }
    }

    private static TokenFilterFactory CreateTokenFilterFactory(
        SearchIndex index,
        CustomAnalyzer analyzer,
        string name)
    {
        var resolved = ResolveComponent(
            index.TokenFilters, analyzer, name, TokenFilterNames, "token filter");

        try
        {
            var factory = TokenFilterFactory.ForName(resolved.SpiName, resolved.Args);
            Inform(factory, resolved);
            return factory;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
        {
            throw ComponentFailure(analyzer, name, "token filter", resolved, ex);
        }
    }

    private static CharFilterFactory CreateCharFilterFactory(
        SearchIndex index,
        CustomAnalyzer analyzer,
        string name)
    {
        var resolved = ResolveComponent(
            index.CharFilters, analyzer, name, CharFilterNames, "char filter");

        try
        {
            var factory = CharFilterFactory.ForName(resolved.SpiName, resolved.Args);
            Inform(factory, resolved);
            return factory;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
        {
            throw ComponentFailure(analyzer, name, "char filter", resolved, ex);
        }
    }

    /// <summary>
    /// Gives a factory that loads part of its configuration from a resource the chance to do so.
    /// </summary>
    /// <remarks>
    /// Not optional for the factories that implement it. A <c>StopFilterFactory</c> built but
    /// never informed leaves its word set null and throws a
    /// <see cref="NullReferenceException"/> from inside the token stream on the first document
    /// analyzed — long after the definition that caused it, and with nothing in the message to
    /// connect the two.
    ///
    /// The resources are served from memory rather than disk; see
    /// <see cref="InlineResourceLoader"/>.
    /// </remarks>
    private static void Inform(object factory, ResolvedComponent resolved)
    {
        if (factory is IResourceLoaderAware aware)
        {
            aware.Inform(new InlineResourceLoader(resolved.Resources));
        }
    }

    /// <summary>
    /// A component resolved to the Lucene factory that implements it.
    /// </summary>
    /// <param name="Resources">
    /// Virtual files backing the options Azure supplies inline but Lucene reads from a path.
    /// </param>
    /// <param name="IsBareReference">
    /// The name resolved directly to a built-in, with nothing in the index defining it.
    /// </param>
    private sealed record ResolvedComponent(
        string SpiName,
        IDictionary<string, string> Args,
        IDictionary<string, string> Resources,
        bool IsBareReference);

    /// <summary>
    /// Resolves a component the analyzer names into the Lucene SPI name and the arguments its
    /// factory should be built with.
    /// </summary>
    /// <remarks>
    /// A name refers either to a component the index defines — which supplies options — or
    /// directly to a built-in, which has none beyond its defaults. Azure allows both, and a
    /// definition takes precedence, since that is the only way a built-in's options can be set.
    /// </remarks>
    private static ResolvedComponent ResolveComponent(
        IEnumerable<AnalysisComponentDefinition> defined,
        CustomAnalyzer analyzer,
        string name,
        IReadOnlyDictionary<string, string> nameMap,
        string kind)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AnalyzerDefinitionException(
                $"Custom analyzer '{analyzer.Name}' names an empty {kind}.");
        }

        var definition = defined.FirstOrDefault(
            i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

        // A defined component's own type is what identifies the Lucene factory; its name is
        // just the label the analyzer refers to it by. Falling back to the name covers a
        // built-in used without a definition, where the name is the identifier.
        var typeName = definition?.ODataType is { } odataType
            ? StripODataPrefix(odataType)
            : name;

        if (UnsupportedComponents.Contains(typeName) || UnsupportedComponents.Contains(name))
        {
            throw new AnalyzerDefinitionException(
                $"Custom analyzer '{analyzer.Name}' names {kind} '{name}', which is not " +
                "supported: it has no Lucene equivalent in the emulator.");
        }

        var spiName = nameMap.TryGetValue(typeName, out var mapped)
            ? mapped
            : typeName.ToLowerInvariant();

        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MatchVersionArg] = MatchVersionValue
        };

        // Seeded before the definition's own options so that anything spelled out there wins.
        if (ComponentDefaults.TryGetValue(spiName, out var defaults))
        {
            foreach (var (option, value) in defaults)
            {
                args[option] = value;
            }
        }

        var resources = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in definition?.AdditionalProperties ?? [])
        {
            if (ToFactoryArgument(value) is not { } argument)
            {
                continue;
            }

            var optionName = TranslateOptionName(spiName, key);

            if (InlineResourceOptions.TryGetValue(optionName, out var resourceOption))
            {
                // Lucene reads this option as a path and Azure supplies its content, so the
                // content is published as a virtual file and the option points at it. The list
                // arrives comma-separated from ToFactoryArgument; the file formats these
                // factories parse are newline-delimited.
                var resourceName = $"_inline_{optionName}";

                resources[resourceName] = resourceOption.FormatContent(argument);
                args[resourceOption.ArgumentName] = resourceName;

                if (resourceOption.Format is { } format)
                {
                    args["format"] = format;
                }

                continue;
            }

            args[optionName] = argument;
        }

        return new ResolvedComponent(spiName, args, resources, definition is null);
    }

    /// <summary>
    /// Arguments Azure treats as optional-with-a-default that the Lucene factory requires.
    /// </summary>
    /// <remarks>
    /// Naming a built-in without defining it is how Azure says "use this with its defaults",
    /// and for most components that works here, because the Lucene factory defaults the same
    /// options. These are the ones where it does not: the factory throws
    /// <c>missing parameter</c> for an option Azure documents a default for, so the bare form
    /// of a perfectly valid Azure definition fails at index creation (issue #73).
    ///
    /// Only components whose options are <em>all</em> optional in Azure belong here. Several
    /// others also fail bare — <c>pattern_replace</c>, <c>pattern_capture</c>, <c>synonym</c>
    /// and <c>dictionary_decompounder</c> — but Azure marks their central option required, so
    /// rejecting them is correct and defaulting them would invent behaviour Azure does not
    /// have. <see cref="RequiredOptions"/> only improves what a bare reference to one of them
    /// is rejected with.
    ///
    /// Keyed by SPI name, so a definition and a bare name resolve to the same defaults.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ComponentDefaults =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            // Azure: pattern defaults to \W+ (runs of non-word characters) and group to -1,
            // which treats the expression as a separator rather than as the token itself. The
            // same defaults the built-in "pattern" analyzer is given in AnalyzerHelper.
            ["pattern"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pattern"] = @"\W+",
                ["group"] = "-1",
            },

            // Azure: min 0, max 300 — the same ceiling it puts on a maxTokenLength.
            ["length"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["min"] = "0",
                ["max"] = AnalysisComponentJson.MaxTokenLengthLimit.ToString(),
            },

            // Azure: maxTokenCount 1. Aggressive, but it is what the service documents, and a
            // chain naming "limit" bare gets the same one token there.
            ["limittokencount"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["maxTokenCount"] = "1",
            },
        };

    /// <summary>
    /// The option Azure marks required on a component, keyed by SPI name, spelled as an index
    /// definition would spell it.
    /// </summary>
    /// <remarks>
    /// These are the components that cannot be named on their own, because Azure gives their
    /// central option no default to fall back on. Lucene reports the omission by wrapping the
    /// factory's <c>missing parameter</c> complaint in an <c>SPI class ... cannot be
    /// instantiated ... likely due to a missing reference of the .NET Assembly</c> message,
    /// which sends the reader looking for a packaging problem that is not there. Naming the
    /// option lets the error say what a bare reference actually left out (issue #73).
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> RequiredOptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["patternreplace"] = "pattern",
            ["patterncapturegroup"] = "patterns",
            ["synonym"] = "synonyms",
            ["dictionarycompoundword"] = "wordList",
        };

    /// <summary>
    /// Azure writes an n-gram's bounds as <c>minGram</c>/<c>maxGram</c>; Lucene's factories read
    /// <c>minGramSize</c>/<c>maxGramSize</c>. Shared by the tokenizer and the filter, which take
    /// the same options.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NGramOptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minGram"] = "minGramSize",
            ["maxGram"] = "maxGramSize",
        };

    /// <summary>
    /// Azure option names that differ from the argument the Lucene factory reads.
    /// </summary>
    /// <remarks>
    /// Keyed by SPI name where the same Azure option means different things to different
    /// factories, so that <c>length</c> on a truncate filter is not confused with the
    /// <c>length</c> options elsewhere.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> OptionNames =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["ngram"] = NGramOptions,
            ["edgengram"] = NGramOptions,
            ["truncate"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["length"] = "prefixLength",
            },
        };

    private static string TranslateOptionName(string spiName, string option)
        => OptionNames.TryGetValue(spiName, out var map) && map.TryGetValue(option, out var mapped)
            ? mapped
            : option;

    /// <summary>
    /// Options Azure supplies inline that Lucene's factories instead read from a file.
    /// </summary>
    /// <remarks>
    /// Azure puts stopword lists, mappings and the like directly in the index definition, while
    /// the Lucene factories that consume them were built for a filesystem and take a path.
    /// Rather than write temporary files, the content is served from memory — see
    /// <see cref="InlineResourceLoader"/> — and these entries say which argument carries the
    /// path and what format the factory should parse the content as.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, InlineResourceOption> InlineResourceOptions =
        new Dictionary<string, InlineResourceOption>(StringComparer.Ordinal)
        {
            // A bare word list, one per line, rather than the Solr-style default.
            ["stopwords"] = new("words", "wordset"),
            ["words"] = new("words", "wordset"),
            ["articles"] = new("articles", "wordset"),
            ["keywords"] = new("protected", "wordset"),

            // The mapping char filter's own format, which the factory parses itself.
            ["mappings"] = new("mapping", Format: null, IsMapping: true),
        };

    /// <summary>
    /// One option whose value Azure supplies inline and Lucene reads from a resource.
    /// </summary>
    /// <param name="ArgumentName">The factory argument that carries the resource name.</param>
    /// <param name="Format">
    /// The <c>format</c> the factory should parse the content as, or null to leave it at the
    /// factory's default.
    /// </param>
    /// <param name="IsMapping">
    /// Whether the content is a list of mapping rules, which have a syntax of their own.
    /// </param>
    private sealed record InlineResourceOption(string ArgumentName, string? Format, bool IsMapping = false)
    {
        /// <summary>
        /// Renders the comma-joined value into the file content the factory parses.
        /// </summary>
        /// <remarks>
        /// Both formats are one entry per line. Mapping rules need more than that: Azure writes
        /// a rule as <c>a=&gt;b</c>, while Lucene's parser requires each side to be a quoted
        /// string — <c>"a" =&gt; "b"</c> — and rejects the bare form outright.
        /// </remarks>
        public string FormatContent(string value)
        {
            var entries = value.Split(',', StringSplitOptions.RemoveEmptyEntries);

            return string.Join("\n", IsMapping ? entries.Select(ToMappingRule) : entries);
        }

        private static string ToMappingRule(string rule)
        {
            var separator = rule.IndexOf("=>", StringComparison.Ordinal);

            // Left alone if it does not look like a rule, so that Lucene reports the malformed
            // rule itself rather than this quoting a fragment of one and obscuring the problem.
            if (separator < 0)
            {
                return rule;
            }

            var from = rule[..separator];
            var to = rule[(separator + 2)..];

            return $"{Quote(from)} => {Quote(to)}";
        }

        /// <summary>
        /// Wraps a mapping rule's side in the quotes Lucene's parser requires, unless the client
        /// already did.
        /// </summary>
        private static string Quote(string value)
        {
            var trimmed = value.Trim();

            return trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"')
                ? trimmed
                : $"\"{trimmed.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }
    }

    /// <summary>
    /// Renders one option of a component definition as the string its Lucene factory parses.
    /// </summary>
    /// <remarks>
    /// Lucene's factories take a flat string map and do their own parsing, so every option has
    /// to arrive as text. Arrays become the comma-separated form the factories already split on
    /// — that is how <c>stopwords</c>, <c>mappings</c> and <c>articles</c> are written when
    /// given inline rather than in a file.
    ///
    /// Returns null for a JSON null, so an explicitly-null option is left at the factory's own
    /// default rather than being passed as the string "null".
    /// </remarks>
    private static string? ToFactoryArgument(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array => string.Join(
                ",",
                value.EnumerateArray()
                    .Select(ToFactoryArgument)
                    .Where(i => i != null)),
            _ => value.GetRawText()
        };
    }

    /// <summary>
    /// Reduces <c>#Microsoft.Azure.Search.EdgeNGramTokenFilterV2</c> to <c>EdgeNGram</c>, the
    /// part that identifies the component.
    /// </summary>
    /// <remarks>
    /// Azure's discriminators are the component's name in Pascal case with its family and
    /// version appended. Stripping those leaves a string that matches the SPI name once
    /// lowercased, which is what lets the built-in components resolve without a mapping entry
    /// each.
    /// </remarks>
    private static string StripODataPrefix(string odataType)
    {
        var name = odataType;

        var lastDot = name.LastIndexOf('.');

        if (lastDot >= 0)
        {
            name = name[(lastDot + 1)..];
        }

        foreach (var suffix in ComponentSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return name;
    }

    /// <summary>
    /// Family and version suffixes carried by a component discriminator, longest first so that
    /// <c>TokenFilterV2</c> is stripped whole rather than leaving a trailing <c>V2</c>.
    /// </summary>
    private static readonly string[] ComponentSuffixes =
    [
        "TokenFilterV2",
        "CharFilterV2",
        "TokenizerV2",
        "TokenFilter",
        "CharFilter",
        "Tokenizer",
    ];

    private static AnalyzerDefinitionException ComponentFailure(
        CustomAnalyzer analyzer,
        string name,
        string kind,
        ResolvedComponent resolved,
        Exception cause)
    {
        // A bare reference to a component whose required option has no default is a cause the
        // emulator can name exactly, so it is reported on its own rather than folded into the
        // general advice below. Only when the reference is bare: once a definition supplies
        // options, whether one of them satisfies the factory is between the two of them, and
        // claiming a supplied option is missing would send the reader somewhere useless.
        if (resolved.IsBareReference && RequiredOptions.TryGetValue(resolved.SpiName, out var required))
        {
            return new AnalyzerDefinitionException(
                $"Custom analyzer '{analyzer.Name}' names {kind} '{name}' on its own, but Azure " +
                $"gives '{required}' no default, so it cannot be used without a definition. " +
                $"Define the {kind} with '{required}' set and name that definition instead.",
                cause);
        }

        // Lucene reports both an unknown SPI name and a failure to construct a known one as the
        // same "cannot be instantiated" ArgumentException, mentioning a missing assembly. That
        // is a misleading thing to hand back for what is nearly always a mistyped name or a
        // missing required option, so the message says what the emulator actually knows and
        // keeps Lucene's text as the inner exception.
        return new AnalyzerDefinitionException(
            $"Custom analyzer '{analyzer.Name}' names {kind} '{name}', which the emulator could " +
            "not build. Check that the name is a supported built-in and that any required " +
            "options are set.",
            cause);
    }

    /// <summary>
    /// Turns a declared stopword list into the set Lucene's analyzers take.
    /// </summary>
    /// <remarks>
    /// An empty list means no stopwords, which is Azure's default for these analyzers, and is
    /// distinct from passing null — several Lucene ctors read null as "use the English set".
    /// </remarks>
    private static CharArraySet ToStopwordSet(IList<string> stopwords)
    {
        var set = new CharArraySet(Version, stopwords.Count, ignoreCase: true);

        foreach (var stopword in stopwords)
        {
            set.Add(stopword);
        }

        return set;
    }
}

/// <summary>
/// Serves an analysis factory's resources from memory instead of the filesystem.
/// </summary>
/// <remarks>
/// Several Lucene factories — the stopword filters, the elision filter, the mapping char filter
/// — take a filename and read their configuration through an <see cref="IResourceLoader"/>.
/// Azure has no filesystem to refer to and puts the same configuration inline in the index
/// definition, so the content is registered here under a synthetic name and the factory is
/// pointed at that. It never learns the difference.
///
/// Writing temporary files would work too, and is what the default loader expects, but it would
/// give an index definition a footprint on disk outside the index directory and a lifetime to
/// manage. The content is a few lines of text held for as long as the analyzer, so memory is
/// the simpler place for it.
/// </remarks>
internal sealed class InlineResourceLoader(IDictionary<string, string> resources) : IResourceLoader
{
    public Stream OpenResource(string resource)
    {
        if (!resources.TryGetValue(resource, out var content))
        {
            // A factory asking for a resource that was never registered means an option naming
            // a real file, which the emulator has no way to resolve.
            throw new IOException(
                $"Resource '{resource}' is not available: the emulator serves only the values " +
                "given inline in the index definition.");
        }

        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    public Type FindType(string cname)
        => Type.GetType(cname)
           ?? throw new ArgumentException($"Type '{cname}' could not be found.", nameof(cname));

    public T NewInstance<T>(string cname)
        => (T)Activator.CreateInstance(FindType(cname))!;
}

/// <summary>
/// Raised when an index's analyzer definitions cannot be built into a Lucene analyzer.
/// </summary>
/// <remarks>
/// Carries a message naming the analyzer and the component at fault, so that
/// <see cref="Indexing.AnalyzerValidator"/> can report it against the definition that caused it
/// and the caller gets Azure's error envelope rather than a 500.
/// </remarks>
public class AnalyzerDefinitionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
