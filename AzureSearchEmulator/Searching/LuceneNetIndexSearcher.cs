using System.Text.Json;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Analysis;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Flexible.Standard;
using Lucene.Net.QueryParsers.Simple;
using Lucene.Net.Search;
using Microsoft.OData.UriParser;
using Microsoft.Spatial;
using Operator = Lucene.Net.QueryParsers.Flexible.Standard.Config.StandardQueryConfigHandler.Operator;

namespace AzureSearchEmulator.Searching;

public class LuceneNetIndexSearcher(ILuceneIndexReaderFactory indexReaderFactory) : IIndexSearcher
{
    public Task<JsonObject?> GetDoc(SearchIndex index, string key, string? select = null)
    {
        var searcher = GetSearcher(index);

        var keyField = index.GetKeyField();

        var docs = searcher.Search(new TermQuery(new Term(keyField.Name, key)), 1);

        if (docs.TotalHits == 0)
        {
            return Task.FromResult<JsonObject?>(null);
        }

        var doc = searcher.Doc(docs.ScoreDocs[0].Doc);

        var result = ConvertSearchDoc(index, doc, FieldSelection.Parse(index, select));

        return Task.FromResult<JsonObject?>(result);
    }

    public Task<int> GetDocCount(SearchIndex index)
    {
        try
        {
            var reader = indexReaderFactory.GetIndexReader(index.Name);

            return Task.FromResult(reader.NumDocs);
        }
        catch (DirectoryNotFoundException)
        {
            // If we've come this far, the index exists, but has no documents, so it's okay to return 0
            return Task.FromResult(0);
        }
    }

    public Task<SearchResponse> Search(SearchIndex index, SearchRequest request)
    {
        // Refused before anything else, so a request asking for a vector search never gets
        // ordinary text results back as though it had been honoured (issue #46).
        if (VectorSearchSupport.GetUnsupportedQueryMessage(request) is { } vectorError)
        {
            throw new InvalidOperationException(vectorError);
        }

        var searcher = GetSearcher(index);

        // Resolved before the query is built because a scoring profile shapes both halves of
        // it — the text weights and the function boosts (issue #47).
        var profile = ScoringProfileSupport.Resolve(index, request.ScoringProfile);
        var scoringParameters = ScoringParameterCollection.Parse(request.ScoringParameters);

        if (profile != null
            && ScoringProfileSupport.GetMissingParameterMessage(profile, scoringParameters) is { } missing)
        {
            throw new InvalidOperationException(missing);
        }

        // Captured once so every document in the response is scored against the same instant;
        // a freshness function reading the clock per document would rank inconsistently within
        // a single query.
        var query = GetQueryFromRequest(index, request, profile, scoringParameters, DateTimeOffset.UtcNow);

        // Facet expressions are parsed even when nothing can match, so that an invalid one is
        // still reported as the error it is rather than silently returning no facets.
        var facets = FacetRequest.Parse(index, request.Facets);

        if (query == null)
        {
            return Task.FromResult(new SearchResponse
            {
                Count = 0,
                // An empty match set still produces the requested facets, with every bucket
                // at zero for a range facet and no buckets at all for a value facet.
                Facets = facets == null ? null : FacetCounter.Empty(facets),
                // Coverage describes how much of the index was searched, not how much of it
                // matched, so a query that cannot match anything still reports it in full.
                Coverage = SearchCoverage.GetCoverage(request),
            });
        }

        var filter = GetFilterFromRequest(request, index);

        var sort = GetSortFromRequest(index, request);

        var highlighter = GetHighlighterFromRequest(index, request, query);

        var selection = FieldSelection.Parse(index, request.Select);

        var hitsWanted = request.Skip + request.Top;

        var response = new SearchResponse { Coverage = SearchCoverage.GetCoverage(request) };

        // $top=0 asks for no documents at all, which is how a caller requests just a count or
        // just the facet structure. Lucene will not build a collector for zero hits, so the
        // document pass is skipped entirely; the count and the facets below do not depend on
        // it. TotalHitCountCollector still gives an accurate $count.
        if (hitsWanted == 0)
        {
            if (request.Count)
            {
                var counter = new TotalHitCountCollector();
                searcher.Search(query, filter, counter);
                response.Count = counter.TotalHits;
            }

            if (facets != null)
            {
                response.Facets = FacetCounter.Count(searcher, facets, query, filter);
            }

            return Task.FromResult(response);
        }

        var docs = searcher.Search(query, filter, hitsWanted, sort, true, true);

        for (var i = request.Skip; i < docs.ScoreDocs.Length; i++)
        {
            var scoreDoc = docs.ScoreDocs[i];

            var doc = searcher.Doc(scoreDoc.Doc);

            var result = ConvertSearchDoc(index, doc, selection);

            result["@search.score"] = scoreDoc.Score;

            if (highlighter != null)
            {
                var highlights = highlighter.GetHighlights(searcher.IndexReader, scoreDoc.Doc, doc);
                result["@search.highlights"] = JsonSerializer.SerializeToNode(highlights);
            }

            response.Results.Add(result);
        }

        if (request.Count)
        {
            response.Count = docs.TotalHits;
        }

        if (facets != null)
        {
            // Counted over the whole match set rather than the page above, so paging never
            // changes the facet counts.
            response.Facets = FacetCounter.Count(searcher, facets, query, filter);
        }

        return Task.FromResult(response);
    }

    public Task<SuggestResponse> Suggest(SearchIndex index, SuggestRequest request)
    {
        var sourceFields = SuggesterSupport.ResolveSourceFields(index, request.SuggesterName, request.SearchFields);

        var terms = SuggesterSupport.Tokenize(request.Search);

        var response = new SuggestResponse { Coverage = GetSuggesterCoverage(request.MinimumCoverage) };

        var query = SuggesterSupport.BuildQuery(request.Search, sourceFields, request.Fuzzy);

        if (query == null)
        {
            return Task.FromResult(response);
        }

        var searcher = GetSearcher(index);

        // Reuses the search path's $filter and $orderby handling so a suggestion is narrowed
        // and ordered exactly as a search result would be.
        var filter = GetFilterFromRequest(new SearchRequest { Filter = request.Filter }, index);
        var sort = GetSortFromRequest(index, new SearchRequest { Orderby = request.Orderby });

        var selection = FieldSelection.Parse(index, request.Select);

        // Over-fetched because documents whose stored text does not actually contain the terms
        // are dropped below, and dropping them after a $top-sized fetch would return fewer
        // suggestions than the caller asked for while more were available.
        var hitsWanted = Math.Min(request.Top * 4, SuggesterSupport.MaxTop * 4);

        var docs = searcher.Search(query, filter, hitsWanted, sort, true, true);

        foreach (var scoreDoc in docs.ScoreDocs)
        {
            if (response.Results.Count >= request.Top)
            {
                break;
            }

            var doc = searcher.Doc(scoreDoc.Doc);

            var text = SuggestionTextBuilder.GetSuggestionText(sourceFields, doc.Get, terms, request.Fuzzy);

            if (text == null)
            {
                continue;
            }

            var result = ConvertSearchDoc(index, doc, selection);

            result["@search.text"] = SuggestionTextBuilder.ApplyHighlighting(
                text, terms, request.HighlightPreTag, request.HighlightPostTag);

            response.Results.Add(result);
        }

        return Task.FromResult(response);
    }

    public Task<AutocompleteResponse> Autocomplete(SearchIndex index, AutocompleteRequest request)
    {
        var sourceFields = SuggesterSupport.ResolveSourceFields(index, request.SuggesterName, request.SearchFields);

        var terms = SuggesterSupport.Tokenize(request.Search);

        var response = new AutocompleteResponse { Coverage = GetSuggesterCoverage(request.MinimumCoverage) };

        var query = SuggesterSupport.BuildQuery(request.Search, sourceFields, request.Fuzzy);

        if (query == null)
        {
            return Task.FromResult(response);
        }

        var searcher = GetSearcher(index);

        var filter = GetFilterFromRequest(new SearchRequest { Filter = request.Filter }, index);

        var completeTerms = terms.GetRange(0, terms.Count - 1);
        var partialTerm = terms[^1];

        // Autocomplete returns distinct completions rather than one entry per document, so
        // every match has to be considered: the same completion often occurs in many documents,
        // and stopping at $top documents would cap the distinct completions well below it.
        var docs = searcher.Search(query, filter, SuggesterSupport.MaxTop * 10);

        // Ordered by first appearance, which follows Lucene's relevance order, while the set
        // stays free of the duplicates the same word in many documents would otherwise produce.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scoreDoc in docs.ScoreDocs)
        {
            if (seen.Count >= request.Top)
            {
                break;
            }

            var doc = searcher.Doc(scoreDoc.Doc);

            foreach (var sourceField in sourceFields)
            {
                var text = doc.Get(sourceField);

                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var completions = request.AutocompleteMode.Equals(
                    AutocompleteModes.OneTermWithContext, StringComparison.OrdinalIgnoreCase)
                    ? SuggestionTextBuilder.GetContextualCompletions(text, completeTerms, partialTerm)
                    : SuggestionTextBuilder.GetCompletions(text, partialTerm, request.AutocompleteMode);

                foreach (var completion in completions)
                {
                    if (seen.Count >= request.Top)
                    {
                        break;
                    }

                    if (!seen.Add(completion))
                    {
                        continue;
                    }

                    response.Results.Add(new AutocompleteItem(
                        SuggestionTextBuilder.ApplyHighlighting(
                            completion, [partialTerm], request.HighlightPreTag, request.HighlightPostTag),
                        BuildQueryPlusText(request.Search, completion)));
                }
            }
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// Replaces the partial final term of the caller's search text with the completion, which
    /// is what a typeahead box puts back into the input.
    /// </summary>
    /// <remarks>
    /// The completed portion of the query is preserved as the caller typed it, so only the
    /// word being completed changes.
    /// </remarks>
    private static string BuildQueryPlusText(string? search, string completion)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return completion;
        }

        var trimmed = search.TrimEnd();
        var lastSpace = trimmed.LastIndexOf(' ');

        return lastSpace < 0
            ? completion
            : string.Concat(trimmed.AsSpan(0, lastSpace + 1), completion);
    }

    /// <summary>
    /// Coverage for a suggest or autocomplete call, which follows the same rule as a search:
    /// reported only when the caller supplied <c>minimumCoverage</c>. See <see cref="SearchCoverage"/>.
    /// </summary>
    private static double? GetSuggesterCoverage(double? minimumCoverage)
        => minimumCoverage == null ? null : SearchCoverage.Full;

    private static HitHighlighter? GetHighlighterFromRequest(SearchIndex index, SearchRequest request, Query query)
    {
        if (string.IsNullOrEmpty(request.Highlight))
        {
            return null;
        }

        var fields = request.Highlight.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var highlightFields = new List<HighlightField>(fields.Length);

        foreach (var field in fields)
        {
            var fieldParts = field.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int maxHighlights = 5;

            // Sub-fields of a complex type are addressed by path, i.e. "Address/City".
            if (!ComplexTypeSupport.TryResolvePath(index, fieldParts[0], out var indexField, out var highlightPath))
            {
                throw new InvalidOperationException($"Unable to find field '{fieldParts[0]}' in the index '{index.Name}'");
            }

            if (fieldParts.Length == 2)
            {
                if (!int.TryParse(fieldParts[1], out maxHighlights))
                {
                    throw new InvalidOperationException($"Unable to parse max highlights expression as int");
                }
            }

            highlightFields.Add(new HighlightField(indexField, maxHighlights, highlightPath));
        }

        return new HitHighlighter(query, request.HighlightPreTag ?? "<em>", request.HighlightPostTag ?? "</em>", highlightFields);
    }

    private static Sort GetSortFromRequest(SearchIndex index, SearchRequest request)
    {
        if (string.IsNullOrEmpty(request.Orderby))
        {
            return Sort.RELEVANCE;
        }

        // NOTE: the ASP.NET OData stuff for parsing $orderby is unfortunately internal.
        // TODO: Replace this with a better parser, maybe with ANTLR?
        var parts = SplitOrderByClauses(request.Orderby);

        if (parts.Length == 0)
        {
            return Sort.RELEVANCE;
        }

        if (parts.Length > 32)
        {
            throw new InvalidOperationException("There is a limit of 32 clauses for $orderby");
        }

        var fields = new SortField[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            fields[i] = GetSortField(index, parts[i]);
        }

        return new Sort(fields);
    }

    /// <summary>
    /// Splits an $orderby expression on its top-level commas.
    /// </summary>
    /// <remarks>
    /// A plain <c>Split(',')</c> would tear apart a clause like
    /// <c>geo.distance(Location, geography'POINT(-122 47)') asc</c>, whose arguments contain
    /// commas of their own, so commas inside parentheses are skipped over here.
    /// </remarks>
    private static string[] SplitOrderByClauses(string orderby)
    {
        var clauses = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < orderby.Length; i++)
        {
            switch (orderby[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    clauses.Add(orderby[start..i]);
                    start = i + 1;
                    break;
            }
        }

        clauses.Add(orderby[start..]);

        return clauses
            .Select(i => i.Trim())
            .Where(i => !string.IsNullOrEmpty(i))
            .ToArray();
    }

    private static SortField GetSortField(SearchIndex index, string sort)
    {
        if (sort.StartsWith("geo.distance", StringComparison.OrdinalIgnoreCase))
        {
            return GetGeoDistanceSortField(index, sort);
        }

        var sortParts = sort.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (sortParts.Length is 0 or > 2)
        {
            // 0 case should not happen, code that calls this removes empty entries. Could happen with multiple spaces.
            throw new InvalidOperationException("Unable to parse $orderby field expression");
        }

        bool descending = sortParts.Length == 2 && sortParts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);

        string fieldName = sortParts[0];

        // A sub-field of a complex type is addressed by its path, i.e. "Address/City", and
        // is indexed under that same name. The schema's casing wins over the caller's, since
        // Lucene field names are case-sensitive.
        if (!ComplexTypeSupport.TryResolvePath(index, fieldName, out var field, out var fieldPath))
        {
            throw new InvalidOperationException($"Unable to find field '{fieldName}' in the index '{index.Name}'");
        }

        if (field.IsComplex())
        {
            throw new InvalidOperationException(
                $"Field '{fieldName}' is of type {field.Type} and cannot be sorted on; sort by one of its sub-fields instead.");
        }

        // A sub-field beneath a Collection(Edm.ComplexType) has one value per element, so
        // there is no single value to order by. Azure Search rejects this rather than
        // picking an arbitrary element.
        if (ComplexTypeSupport.FindComplexCollectionAncestorPath(index, fieldPath) is { } collectionPath)
        {
            throw new InvalidOperationException(
                $"Field '{fieldName}' cannot be sorted on because '{collectionPath}' is a {ComplexTypeSupport.ComplexCollectionType}.");
        }

        return new SortField(fieldPath, GetSortFieldType(field), descending);
    }

    /// <summary>
    /// Builds the sort for a <c>geo.distance(field, geography'POINT(lon lat)') asc|desc</c>
    /// order-by clause.
    /// </summary>
    private static SortField GetGeoDistanceSortField(SearchIndex index, string sort)
    {
        // The direction is the trailing token, and the rest is a function call the OData
        // parser can read for us, so we don't have to parse the WKT literal by hand.
        var descending = false;
        var expression = sort;

        var lastSpace = sort.LastIndexOf(' ');

        if (lastSpace > 0)
        {
            var direction = sort[(lastSpace + 1)..].Trim();

            if (direction.Equals("desc", StringComparison.OrdinalIgnoreCase)
                || direction.Equals("asc", StringComparison.OrdinalIgnoreCase))
            {
                descending = direction.Equals("desc", StringComparison.OrdinalIgnoreCase);
                expression = sort[..lastSpace];
            }
        }

        var parser = new UriQueryExpressionParser(100);

        // ParseFilter expects a boolean expression, so the distance is compared against a
        // throwaway constant purely to give the parser something well-formed to chew on.
        if (parser.ParseFilter($"{expression} le 0") is not BinaryOperatorToken
            {
                Left: FunctionCallToken { Name: "geo.distance" } functionToken
            })
        {
            throw new InvalidOperationException($"Unable to parse $orderby expression '{sort}'.");
        }

        var args = functionToken.Arguments.ToList();

        if (args.Count != 2)
        {
            throw new InvalidOperationException("geo.distance requires two arguments");
        }

        var pathToken = args.Select(i => i.ValueToken).OfType<EndPathToken>().FirstOrDefault();
        var pointLiteral = args.Select(i => i.ValueToken).OfType<LiteralToken>().FirstOrDefault();

        if (pathToken is null || pointLiteral?.Value is not GeographyPoint origin)
        {
            throw new InvalidOperationException(
                "geo.distance requires one field path argument and one geography point constant.");
        }

        var field = index.Fields.FirstOrDefault(i =>
            i.Name.Equals(pathToken.Identifier, StringComparison.OrdinalIgnoreCase));

        if (field == null)
        {
            throw new InvalidOperationException(
                $"Unable to find field '{pathToken.Identifier}' in the index '{index.Name}'");
        }

        if (field.Type != GeoSupport.GeographyPointType)
        {
            // Collection(Edm.GeographyPoint) is excluded here as well: Azure Search cannot
            // sort on a collection field, since there is no single distance to sort by.
            throw new InvalidOperationException(
                $"Field '{field.Name}' is of type {field.Type}; sorting by geo.distance requires a {GeoSupport.GeographyPointType} field.");
        }

        if (!field.Sortable.GetValueOrDefault())
        {
            throw new InvalidOperationException($"Field '{field.Name}' is not sortable.");
        }

        return new SortField(
            field.Name,
            new GeoDistanceComparatorSource(field.Name, origin.Longitude, origin.Latitude),
            descending);
    }

    private static SortFieldType GetSortFieldType(SearchField field)
    {
        // Collection fields are not directly sortable in Azure Search; fall through to error.
        return field.Type switch
        {
            "Edm.String" => SortFieldType.STRING,
            "Edm.Int32" => SortFieldType.INT32,
            "Edm.Int64" => SortFieldType.INT64,
            "Edm.Double" => SortFieldType.DOUBLE,
            "Edm.Boolean" => SortFieldType.INT32,
            "Edm.DateTimeOffset" => SortFieldType.INT64,
            "Edm.GeographyPoint" => throw new NotImplementedException(),
            "Edm.ComplexType" => throw new NotImplementedException(),
            _ => throw new InvalidOperationException($"Unsupported field type {field.Type} for sorting")
        };
    }

    private static Filter? GetFilterFromRequest(SearchRequest request, SearchIndex? index = null)
    {
        if (string.IsNullOrEmpty(request.Filter))
        {
            return null;
        }

        var parser = new UriQueryExpressionParser(100);
        var filterQuery = parser.ParseFilter(request.Filter);

        if (filterQuery == null)
        {
            return null;
        }

        var query = filterQuery.Accept(new ODataQueryVisitor(index));

        return new QueryWrapperFilter(query);
    }

    /// <summary>
    /// Builds the query for a request, with the scoring profile's weights applied to the text
    /// match and its functions applied to the resulting scores (issue #47).
    /// </summary>
    /// <remarks>
    /// A profile reaches the query in two places, because its two halves act at different
    /// stages: the text weights have to be present while the query is parsed, since they
    /// weight each field's contribution to the match, whereas the functions adjust the score of
    /// whatever matched and so wrap the finished query.
    ///
    /// Neither half applies to a query that does not rank: see <see cref="IsScored"/>.
    /// </remarks>
    private static Query? GetQueryFromRequest(
        SearchIndex index,
        SearchRequest request,
        ScoringProfile? profile,
        ScoringParameterCollection parameters,
        DateTimeOffset now)
    {
        var query = GetBaseQueryFromRequest(index, request, profile);

        if (query == null || profile == null || !IsScored(request))
        {
            return query;
        }

        return ScoringProfileQuery.Wrap(query, index, profile, parameters, now);
    }

    /// <summary>
    /// Whether a request produces ranked results at all.
    /// </summary>
    /// <remarks>
    /// Azure scores full-text search only: an empty or wildcard search returns a uniform
    /// <c>@search.score</c> of 1, with the filter — not relevance — deciding the result set.
    /// A scoring profile has nothing to act on there, and applying one anyway would invent a
    /// ranking the real service does not produce.
    /// </remarks>
    private static bool IsScored(SearchRequest request)
        => request.Search is not (null or "*" or "*:*");

    private static Query? GetBaseQueryFromRequest(SearchIndex index, SearchRequest request, ScoringProfile? profile)
    {
        if (request.Search == null)
        {
            return new MatchAllDocsQuery();
        }

        // Searchable sub-fields count too, and are addressed by their path.
        var firstTextFieldPath = ComplexTypeSupport.EnumerateLeafFields(index)
            .Where(i => i.Field.Searchable.GetValueOrDefault())
            .Select(i => i.Path)
            .FirstOrDefault();

        if (firstTextFieldPath == null)
        {
            throw new InvalidOperationException("Unable to search with no searchable fields");
        }

        var analyzer = AnalyzerHelper.GetPerFieldSearchAnalyzer(index.Fields);

        return request.QueryType switch
        {
            "full" => ParseFullQuery(index, request, firstTextFieldPath, analyzer, profile),
            _ => ParseSimpleQuery(index, request, analyzer, profile)
        };
    }

    private static Query? ParseSimpleQuery(
        SearchIndex index,
        SearchRequest request,
        Analyzer analyzer,
        ScoringProfile? profile)
    {
        if (request.Search == "*" || request.Search == "*:*")
        {
            return new MatchAllDocsQuery();
        }

        var searchFields = GetSearchFields(index, request.SearchFields);

        // Every searched field weighs 1 unless the profile says otherwise (issue #47).
        var weights = ScoringProfileSupport.GetWeights(index, profile, searchFields);

        var queryParser = new SimpleQueryParser(analyzer, weights)
        {
            DefaultOperator = GetDefaultOccur(request.SearchMode),
        };

        return queryParser.Parse(request.Search);
    }

    private static Query? ParseFullQuery(
        SearchIndex index,
        SearchRequest request,
        string? firstTextFieldPath,
        Analyzer analyzer,
        ScoringProfile? profile)
    {
        if (request.Search == "*" || request.Search == "*:*")
        {
            return new MatchAllDocsQuery();
        }

        var queryParser = new StandardQueryParser(analyzer)
        {
            DefaultOperator = GetDefaultOperator(request.SearchMode),
        };

        var query = queryParser.Parse(request.Search, firstTextFieldPath ?? "Text");

        // The full syntax parser takes no weight map, so a profile's text weights are applied
        // to the parsed query instead — otherwise they would apply to simple queries only and
        // be silently dropped here (issue #47).
        return ScoringProfileSupport.ApplyWeights(
            index, profile, query, GetSearchFields(index, request.SearchFields));
    }

    private static Operator GetDefaultOperator(string? searchMode)
    {
        return searchMode switch
        {
            "any" => Operator.OR,
            "all" => Operator.AND,
            _ => Operator.OR
        };
    }

    private static Occur GetDefaultOccur(string? searchMode)
    {
        return searchMode switch
        {
            "any" => Occur.SHOULD,
            "all" => Occur.MUST,
            _ => Occur.SHOULD
        };
    }

    private static IEnumerable<string> GetSearchFields(SearchIndex index, string? searchFields)
    {
        // Searchable sub-fields of a complex type are indexed — and named in searchFields —
        // by their full path, so the candidates come from the leaves rather than the
        // top-level fields.
        var searchable = ComplexTypeSupport.EnumerateLeafFields(index)
            .Where(i => i.Field.Searchable.GetValueOrDefault());

        if (string.IsNullOrEmpty(searchFields))
        {
            return searchable.Select(i => i.Path);
        }

        var fields = searchFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return searchable
            .Where(i => fields.Contains(i.Path, StringComparer.OrdinalIgnoreCase))
            .Select(i => i.Path);
    }

    private static JsonObject ConvertSearchDoc(
        SearchIndex index,
        Lucene.Net.Documents.Document doc,
        FieldSelection? selection = null)
    {
        var result = new JsonObject();

        foreach (var field in index.Fields.Where(i => i.Retrievable))
        {
            // A null selection means no $select was given, so every retrievable field stays.
            if (selection?.Includes(field.Name) == false)
            {
                continue;
            }

            if (field.IsComplex())
            {
                // Complex values round-trip through the stored JSON sidecar rather than the
                // flattened leaves, which cannot say which values belonged to which element
                // of a Collection(Edm.ComplexType).
                var storedComplexJson = doc.Get(ComplexTypeSupport.GetComplexStorageFieldName(field.Name));
                if (storedComplexJson != null)
                {
                    result[field.Name] = FilterToRetrievableSubFields(
                        field,
                        JsonNode.Parse(storedComplexJson),
                        selection?.GetSubSelection(field.Name));
                }
                continue;
            }

            if (field.IsCollection())
            {
                var storedJson = doc.Get(SearchFieldExtensions.GetCollectionStorageFieldName(field.Name));
                if (storedJson != null)
                {
                    result[field.Name] = JsonNode.Parse(storedJson);
                }
                continue;
            }

            if (field.Type == GeoSupport.GeographyPointType)
            {
                // Points are stored as a separate lat/lon pair rather than under the
                // field's own name, so they need their own lookup.
                var latField = doc.GetField(GeoSupport.GetLatFieldName(field.Name));
                var lonField = doc.GetField(GeoSupport.GetLonFieldName(field.Name));

                if (latField?.GetDoubleValue() is double storedLat
                    && lonField?.GetDoubleValue() is double storedLon)
                {
                    result[field.Name] = GeoSupport.CreateGeoJsonPoint(storedLon, storedLat);
                }

                continue;
            }

            var docField = doc.GetField(field.Name);

            if (docField != null)
            {
                result[field.Name] = field.Type switch
                {
                    "Edm.String" => docField.GetStringValue(),
                    "Edm.Int32" => docField.GetInt32Value(),
                    "Edm.Int64" => docField.GetInt64Value(),
                    "Edm.Double" => docField.GetDoubleValue(),
                    "Edm.Boolean" => docField.GetInt32Value() is int i ? i != 0 : null,
                    "Edm.DateTimeOffset" => docField.GetInt64Value() is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
                    // Edm.GeographyPoint is handled above, before this switch.
                    "Edm.ComplexType" => throw new NotImplementedException(),
                    _ => throw new InvalidOperationException($"Unsupported field type {field.Type}")
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Strips sub-fields from a complex value read back out of its stored JSON sidecar: those
    /// marked non-retrievable always, and those outside <paramref name="selection"/> when a
    /// <c>$select</c> narrowed the request.
    /// </summary>
    /// <remarks>
    /// The sidecar deliberately holds the document's original object so that retrieval is
    /// faithful, but that means it also holds sub-fields the schema hides. Azure Search
    /// applies retrievability per sub-field, so they are removed on the way out. Properties
    /// with no matching sub-field are dropped too: they were never part of the schema and
    /// were never indexed.
    ///
    /// A null <paramref name="selection"/> means this value is wanted whole, either because
    /// there was no <c>$select</c> or because the path named the complex field itself. The
    /// same selection applies to every element of a collection, since <c>$select</c> narrows
    /// by path and cannot address one element.
    /// </remarks>
    private static JsonNode? FilterToRetrievableSubFields(
        SearchField field,
        JsonNode? value,
        FieldSelection? selection = null)
    {
        switch (value)
        {
            case null:
                return null;

            case JsonArray array:
            {
                var filtered = new JsonArray();

                foreach (var element in array)
                {
                    filtered.Add(FilterToRetrievableSubFields(field, element, selection));
                }

                return filtered;
            }

            case JsonObject obj:
            {
                var filtered = new JsonObject();

                foreach (var subField in field.Fields.Where(f => f.Retrievable))
                {
                    if (selection?.Includes(subField.Name) == false)
                    {
                        continue;
                    }

                    var subValue = obj.FirstOrDefault(p =>
                        string.Equals(p.Key, subField.Name, StringComparison.OrdinalIgnoreCase)).Value;

                    if (subValue is null)
                    {
                        continue;
                    }

                    filtered[subField.Name] = subField.IsComplex()
                        ? FilterToRetrievableSubFields(
                            subField,
                            subValue.DeepClone(),
                            selection?.GetSubSelection(subField.Name))
                        : subValue.DeepClone();
                }

                return filtered;
            }

            default:
                return value.DeepClone();
        }
    }

    private IndexSearcher GetSearcher(SearchIndex index)
    {
        var reader = indexReaderFactory.GetIndexReader(index.Name);

        return new IndexSearcher(reader);
    }
}
