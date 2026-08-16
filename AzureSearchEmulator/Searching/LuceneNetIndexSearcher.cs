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
    public Task<JsonObject?> GetDoc(SearchIndex index, string key)
    {
        var searcher = GetSearcher(index);

        var keyField = index.GetKeyField();

        var docs = searcher.Search(new TermQuery(new Term(keyField.Name, key)), 1);

        if (docs.TotalHits == 0)
        {
            return Task.FromResult<JsonObject?>(null);
        }

        var doc = searcher.Doc(docs.ScoreDocs[0].Doc);

        var result = ConvertSearchDoc(index, doc);

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
        var searcher = GetSearcher(index);

        var query = GetQueryFromRequest(index, request);

        if (query == null)
        {
            return Task.FromResult(new SearchResponse
            {
                Count = 0,
            });
        }

        var filter = GetFilterFromRequest(request, index);

        var sort = GetSortFromRequest(index, request);

        var highlighter = GetHighlighterFromRequest(index, request, query);

        var docs = searcher.Search(query, filter, request.Skip + request.Top, sort, true, true);

        var response = new SearchResponse();

        for (var i = request.Skip; i < docs.ScoreDocs.Length; i++)
        {
            var scoreDoc = docs.ScoreDocs[i];

            var doc = searcher.Doc(scoreDoc.Doc);

            var result = ConvertSearchDoc(index, doc);

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

        return Task.FromResult(response);
    }

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

            var indexField = index.Fields.FirstOrDefault(i => i.Name.Equals(fieldParts[0], StringComparison.OrdinalIgnoreCase));

            if (indexField == null)
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

            highlightFields.Add(new HighlightField(indexField, maxHighlights));
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

        var field = index.Fields.FirstOrDefault(i => i.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (field == null)
        {
            throw new InvalidOperationException($"Unable to find field '{fieldName}' in the index '{index.Name}'");
        }

        return new SortField(field.Name, GetSortFieldType(field), descending);
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

    private static Query? GetQueryFromRequest(SearchIndex index, SearchRequest request)
    {
        if (request.Search == null)
        {
            return new MatchAllDocsQuery();
        }

        var firstTextField = index.Fields.FirstOrDefault(i => i.Searchable.GetValueOrDefault());

        if (firstTextField == null)
        {
            throw new InvalidOperationException("Unable to search with no searchable fields");
        }

        var analyzer = AnalyzerHelper.GetPerFieldSearchAnalyzer(index.Fields);

        return request.QueryType switch
        {
            "full" => ParseFullQuery(request, firstTextField, analyzer),
            _ => ParseSimpleQuery(index, request, analyzer)
        };
    }

    private static Query? ParseSimpleQuery(SearchIndex index, SearchRequest request, Analyzer analyzer)
    {
        if (request.Search == "*" || request.Search == "*:*")
        {
            return new MatchAllDocsQuery();
        }

        var searchFields = GetSearchFields(index, request.SearchFields);

        var weights = new Dictionary<string, float>(searchFields.Select(i => new KeyValuePair<string, float>(i, 1.0f)));

        var queryParser = new SimpleQueryParser(analyzer, weights)
        {
            DefaultOperator = GetDefaultOccur(request.SearchMode),
        };

        return queryParser.Parse(request.Search);
    }

    private static Query? ParseFullQuery(SearchRequest request, SearchField? firstTextField, Analyzer analyzer)
    {
        if (request.Search == "*" || request.Search == "*:*")
        {
            return new MatchAllDocsQuery();
        }

        var queryParser = new StandardQueryParser(analyzer)
        {
            DefaultOperator = GetDefaultOperator(request.SearchMode),
        };

        return queryParser.Parse(request.Search, firstTextField?.Name ?? "Text");
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
        if (string.IsNullOrEmpty(searchFields))
        {
            return index.Fields.Where(i => i.Searchable.GetValueOrDefault()).Select(i => i.Name);
        }

        var fields = searchFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return index.Fields
            .Where(i => i.Searchable.GetValueOrDefault() && fields.Contains(i.Name, StringComparer.OrdinalIgnoreCase))
            .Select(i => i.Name);
    }

    private static JsonObject ConvertSearchDoc(SearchIndex index, Lucene.Net.Documents.Document doc)
    {
        var result = new JsonObject();

        foreach (var field in index.Fields.Where(i => i.Retrievable))
        {
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

    private IndexSearcher GetSearcher(SearchIndex index)
    {
        var reader = indexReaderFactory.GetIndexReader(index.Name);

        return new IndexSearcher(reader);
    }
}
