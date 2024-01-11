using System.Text.Json;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Analysis;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Flexible.Standard;
using Lucene.Net.QueryParsers.Simple;
using Lucene.Net.Search;
using Microsoft.OData.UriParser;
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
        var reader = indexReaderFactory.GetIndexReader(index.Name);

        return Task.FromResult(reader.NumDocs);
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

        var filter = GetFilterFromRequest(request);

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
        var parts = request.Orderby.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

    private static SortField GetSortField(SearchIndex index, string sort)
    {
        var sortParts = sort.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (sortParts.Length is 0 or > 2)
        {
            // 0 case should not happen, code that calls this removes empty entries. Could happen with multiple spaces.
            throw new InvalidOperationException("Unable to parse $orderby field expression");
        }

        bool descending = sortParts.Length == 2 && sortParts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);

        // TODO: support geospatial distance sorting
        string fieldName = sortParts[0];

        var field = index.Fields.FirstOrDefault(i => i.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (field == null)
        {
            throw new InvalidOperationException($"Unable to find field '{fieldName}' in the index '{index.Name}'");
        }

        return new SortField(field.Name, GetSortFieldType(field), descending);
    }

    private static SortFieldType GetSortFieldType(SearchField field)
    {
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
            "Collection(Edm.String)" => throw new NotImplementedException(),
            "Collection(Edm.Int32)" => throw new NotImplementedException(),
            "Collection(Edm.Int64)" => throw new NotImplementedException(),
            "Collection(Edm.Double)" => throw new NotImplementedException(),
            "Collection(Edm.Boolean)" => throw new NotImplementedException(),
            "Collection(Edm.DateTimeOffset)" => throw new NotImplementedException(),
            "Collection(Edm.GeographyPoint)" => throw new NotImplementedException(),
            "Collection(Edm.ComplexType)" => throw new NotImplementedException(),
            _ => throw new InvalidOperationException($"Unsupported field type {field.Type}")
        };
    }

    private static Filter? GetFilterFromRequest(SearchRequest request)
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

        var query = filterQuery.Accept(new ODataQueryVisitor());

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
                    "Edm.GeographyPoint" => throw new NotImplementedException(),
                    "Edm.ComplexType" => throw new NotImplementedException(),
                    "Collection(Edm.String)" => throw new NotImplementedException(),
                    "Collection(Edm.Int32)" => throw new NotImplementedException(),
                    "Collection(Edm.Int64)" => throw new NotImplementedException(),
                    "Collection(Edm.Double)" => throw new NotImplementedException(),
                    "Collection(Edm.Boolean)" => throw new NotImplementedException(),
                    "Collection(Edm.DateTimeOffset)" => throw new NotImplementedException(),
                    "Collection(Edm.GeographyPoint)" => throw new NotImplementedException(),
                    "Collection(Edm.ComplexType)" => throw new NotImplementedException(),
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
