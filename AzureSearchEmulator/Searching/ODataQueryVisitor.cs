using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Analysis;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Flexible.Standard;
using Lucene.Net.QueryParsers.Simple;
using Lucene.Net.Search;
using Microsoft.OData.UriParser;
using Microsoft.OData.UriParser.Aggregation;
using Operator = Lucene.Net.QueryParsers.Flexible.Standard.Config.StandardQueryConfigHandler.Operator;

namespace AzureSearchEmulator.Searching;

public class ODataQueryVisitor(SearchIndex? index = null) : ISyntacticTreeVisitor<Query>
{
    private readonly SearchIndex? _index = index;
    public Query Visit(AllToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(AnyToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(BinaryOperatorToken tokenIn)
    {
        if (tokenIn.OperatorKind is BinaryOperatorKind.Or or BinaryOperatorKind.And)
        {
            var left = tokenIn.Left.Accept(this);
            var right = tokenIn.Right.Accept(this);
            var occur = GetOccurFromOperator(tokenIn.OperatorKind);

            return new BooleanQuery
            {
                Clauses =
                {
                    new BooleanClause(left, occur),
                    new BooleanClause(right, occur),
                }
            };
        }

        if (tokenIn is
            {
                Left: EndPathToken { Identifier: string path },
                Right: LiteralToken literalToken
            })
        {
            return tokenIn.OperatorKind switch
            {
                BinaryOperatorKind.Equal => HandleEqualComparison(path, literalToken),
                BinaryOperatorKind.LessThan => HandleLessThanComparison(path, literalToken),
                BinaryOperatorKind.LessThanOrEqual => HandleLessThanOrEqualComparison(path, literalToken),
                BinaryOperatorKind.GreaterThan => HandleGreaterThanComparison(path, literalToken),
                BinaryOperatorKind.GreaterThanOrEqual => HandleGreaterThanOrEqualComparison(path, literalToken),
                BinaryOperatorKind.NotEqual => HandleNotEqualComparison(path, literalToken),
                _ => throw new NotImplementedException($"Operator {tokenIn.OperatorKind} not implemented")
            };
        }

        throw new NotImplementedException();
    }

    private static Occur GetOccurFromOperator(BinaryOperatorKind operatorKind)
    {
        return operatorKind switch
        {
            BinaryOperatorKind.Or => Occur.SHOULD,
            BinaryOperatorKind.And => Occur.MUST,
            _ => throw new NotImplementedException()
        };
    }

    private static Query HandleEqualComparison(string path, LiteralToken literalToken)
    {
        return literalToken.Value switch
        {
            string stringValue => new TermQuery(new Term(path, stringValue)),
            int intValue => NumericRangeQuery.NewInt32Range(path, intValue, intValue, true, true),
            long longValue => NumericRangeQuery.NewInt64Range(path, longValue, longValue, true, true),
            float floatValue => NumericRangeQuery.NewSingleRange(path, floatValue, floatValue, true, true),
            double doubleValue => NumericRangeQuery.NewDoubleRange(path, doubleValue, doubleValue, true, true),
            bool boolValue => NumericRangeQuery.NewInt32Range(path, boolValue ? 1 : 0, boolValue ? 1 : 0, true, true),
            _ => throw new NotImplementedException()
        };
    }

    private static Query HandleLessThanComparison(string path, LiteralToken literalToken)
    {
        return literalToken.Value switch
        {
            int intValue => NumericRangeQuery.NewInt32Range(path, int.MinValue, intValue, true, false),
            long longValue => NumericRangeQuery.NewInt64Range(path, long.MinValue, longValue, true, false),
            float floatValue => NumericRangeQuery.NewSingleRange(path, float.NegativeInfinity, floatValue, true, false),
            double doubleValue => NumericRangeQuery.NewDoubleRange(path, double.NegativeInfinity, doubleValue, true, false),
            _ => throw new NotImplementedException($"Less than comparison not supported for type {literalToken.Value?.GetType().Name}")
        };
    }

    private static Query HandleLessThanOrEqualComparison(string path, LiteralToken literalToken)
    {
        return literalToken.Value switch
        {
            int intValue => NumericRangeQuery.NewInt32Range(path, int.MinValue, intValue, true, true),
            long longValue => NumericRangeQuery.NewInt64Range(path, long.MinValue, longValue, true, true),
            float floatValue => NumericRangeQuery.NewSingleRange(path, float.NegativeInfinity, floatValue, true, true),
            double doubleValue => NumericRangeQuery.NewDoubleRange(path, double.NegativeInfinity, doubleValue, true, true),
            _ => throw new NotImplementedException($"Less than or equal comparison not supported for type {literalToken.Value?.GetType().Name}")
        };
    }

    private static Query HandleGreaterThanComparison(string path, LiteralToken literalToken)
    {
        return literalToken.Value switch
        {
            int intValue => NumericRangeQuery.NewInt32Range(path, intValue, int.MaxValue, false, true),
            long longValue => NumericRangeQuery.NewInt64Range(path, longValue, long.MaxValue, false, true),
            float floatValue => NumericRangeQuery.NewSingleRange(path, floatValue, float.PositiveInfinity, false, true),
            double doubleValue => NumericRangeQuery.NewDoubleRange(path, doubleValue, double.PositiveInfinity, false, true),
            _ => throw new NotImplementedException($"Greater than comparison not supported for type {literalToken.Value?.GetType().Name}")
        };
    }

    private static Query HandleGreaterThanOrEqualComparison(string path, LiteralToken literalToken)
    {
        return literalToken.Value switch
        {
            int intValue => NumericRangeQuery.NewInt32Range(path, intValue, int.MaxValue, true, true),
            long longValue => NumericRangeQuery.NewInt64Range(path, longValue, long.MaxValue, true, true),
            float floatValue => NumericRangeQuery.NewSingleRange(path, floatValue, float.PositiveInfinity, true, true),
            double doubleValue => NumericRangeQuery.NewDoubleRange(path, doubleValue, double.PositiveInfinity, true, true),
            _ => throw new NotImplementedException($"Greater than or equal comparison not supported for type {literalToken.Value?.GetType().Name}")
        };
    }

    private static Query HandleNotEqualComparison(string path, LiteralToken literalToken)
    {
        var equalQuery = HandleEqualComparison(path, literalToken);
        return new BooleanQuery
        {
            Clauses =
            {
                new BooleanClause(new MatchAllDocsQuery(), Occur.MUST),
                new BooleanClause(equalQuery, Occur.MUST_NOT)
            }
        };
    }

    public Query Visit(CountSegmentToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(InToken tokenIn)
    {
        if (tokenIn is
            {
                Left: EndPathToken { Identifier: string path },
                Right: LiteralToken { Value: string valueString }
            })
        {
            valueString = valueString.TrimStart('(').TrimEnd(')');

            var values = valueString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var query = new BooleanQuery();

            foreach (var value in values)
            {
                if (value.StartsWith('\'') || value.StartsWith('\"'))
                {
                    query.Add(new TermQuery(new Term(path, value.Trim('\'', '\"'))), Occur.SHOULD);
                }
                else
                {
                    throw new NotImplementedException("Support for non-string IN lists not yet implemented");
                }
            }

            return query;
        }

        throw new NotImplementedException();
    }

    public Query Visit(DottedIdentifierToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(ExpandToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(ExpandTermToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(FunctionCallToken tokenIn)
    {
        return tokenIn.Name switch
        {
            "search.in" => VisitSearchIn(tokenIn),
            "search.ismatch" => VisitSearchIsMatch(tokenIn),
            "search.ismatchscoring" => VisitSearchIsMatchScoring(tokenIn),
            _ => throw new NotImplementedException($"Function {tokenIn.Name} not implemented")
        };
    }

    private static BooleanQuery VisitSearchIn(FunctionCallToken tokenIn)
    {
        var args = tokenIn.Arguments.ToList();

        if (args.Count is < 2 or > 3)
        {
            throw new ArgumentException("search.in requires two or three arguments");
        }

        if (args[0].ValueToken is not EndPathToken { Identifier: string path })
        {
            throw new NotImplementedException("Passing anything other than an end path as the first parameter to search.in is not yet implemented");
        }

        if (args[1].ValueToken is not LiteralToken { Value: string inList })
        {
            throw new NotImplementedException("Passing anything other than a string as the second parameter to search.in is not yet implemented");
        }

        var delimiters = new[] { ',', ' ' };

        if (args.Count == 3)
        {
            if (args[2].ValueToken is not LiteralToken { Value: string delimiterString })
            {
                throw new NotImplementedException("Passing anything other than a string as the third parameter to search.in is not yet implemented");
            }

            delimiters = delimiterString.ToCharArray();
        }

        var values = inList.Split(delimiters);

        var query = new BooleanQuery();

        foreach (var value in values)
        {
            query.Add(new TermQuery(new Term(path, value)), Occur.SHOULD);
        }

        return query;
    }

    public Query Visit(LambdaToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(LiteralToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(InnerPathToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(OrderByToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(EndPathToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(CustomQueryOptionToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(RangeVariableToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(SelectToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(SelectTermToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(StarToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(UnaryOperatorToken tokenIn)
    {
        if (tokenIn.OperatorKind == UnaryOperatorKind.Not)
        {
            var operand = tokenIn.Operand.Accept(this);
            return new BooleanQuery
            {
                Clauses =
                {
                    new BooleanClause(new MatchAllDocsQuery(), Occur.MUST),
                    new BooleanClause(operand, Occur.MUST_NOT)
                }
            };
        }

        throw new NotImplementedException($"Unary operator {tokenIn.OperatorKind} not implemented");
    }

    public Query Visit(FunctionParameterToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(AggregateToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(AggregateExpressionToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(EntitySetAggregateToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(GroupByToken tokenIn)
    {
        throw new NotImplementedException();
    }

    public Query Visit(RootPathToken tokenIn)
    {
        throw new NotImplementedException();
    }

    private Query VisitSearchIsMatch(FunctionCallToken tokenIn)
    {
        return BuildFullTextSearchQuery(tokenIn, includeScoring: false);
    }

    private Query VisitSearchIsMatchScoring(FunctionCallToken tokenIn)
    {
        return BuildFullTextSearchQuery(tokenIn, includeScoring: true);
    }

    private Query BuildFullTextSearchQuery(FunctionCallToken tokenIn, bool includeScoring)
    {
        var args = tokenIn.Arguments.ToList();

        // search.ismatch(searchText) or search.ismatch(searchText, searchFields) or search.ismatch(searchText, searchFields, queryType, searchMode)
        if (args.Count is < 1 or > 4)
        {
            throw new ArgumentException($"search.ismatch requires 1 to 4 arguments, got {args.Count}");
        }

        // First argument: search text (required)
        if (args[0].ValueToken is not LiteralToken { Value: string searchText })
        {
            throw new InvalidOperationException("First argument to search.ismatch must be a string");
        }

        // Second argument: search fields (optional)
        string? searchFields = null;
        if (args.Count >= 2 && args[1].ValueToken is LiteralToken { Value: string fields })
        {
            searchFields = fields;
        }

        // Third argument: query type (optional)
        string queryType = "simple";
        if (args.Count >= 3 && args[2].ValueToken is LiteralToken { Value: string qType })
        {
            queryType = qType;
        }

        // Fourth argument: search mode (optional)
        string searchMode = "any";
        if (args.Count >= 4 && args[3].ValueToken is LiteralToken { Value: string sMode })
        {
            searchMode = sMode;
        }

        if (_index == null)
        {
            throw new InvalidOperationException("SearchIndex is required for search.ismatch function");
        }

        return ParseFullTextSearchQuery(searchText, searchFields, queryType, searchMode);
    }

    private Query ParseFullTextSearchQuery(string searchText, string? searchFields, string queryType, string searchMode)
    {
        if (_index == null)
        {
            throw new InvalidOperationException("SearchIndex is required");
        }

        // Get the analyzer for this index
        var analyzer = AnalyzerHelper.GetPerFieldSearchAnalyzer(_index.Fields);

        // Parse the search text
        var query = queryType switch
        {
            "full" => ParseFullQuery(searchText, analyzer),
            _ => ParseSimpleQuery(searchText, searchFields, searchMode, analyzer)
        };

        // Apply field restrictions if specified
        if (!string.IsNullOrEmpty(searchFields))
        {
            query = RestrictQueryToFields(query, searchFields);
        }

        return query;
    }

    private Query ParseSimpleQuery(string searchText, string? searchFields, string searchMode, Analyzer analyzer)
    {
        if (searchText == "*" || searchText == "*:*")
        {
            return new MatchAllDocsQuery();
        }

        var fieldsToSearch = GetSearchFieldsForQuery(searchFields);

        if (fieldsToSearch.Count == 0)
        {
            // If no specific fields, use all searchable fields
            fieldsToSearch = _index!.Fields
                .Where(i => i.Searchable.GetValueOrDefault())
                .Select(i => i.Name)
                .ToList();
        }

        var weights = new Dictionary<string, float>(
            fieldsToSearch.Select(i => new KeyValuePair<string, float>(i, 1.0f))
        );

        var queryParser = new SimpleQueryParser(analyzer, weights)
        {
            DefaultOperator = GetDefaultOccur(searchMode),
        };

        return queryParser.Parse(searchText);
    }

    private Query ParseFullQuery(string searchText, Analyzer analyzer)
    {
        if (searchText == "*" || searchText == "*:*")
        {
            return new MatchAllDocsQuery();
        }

        var firstTextField = _index?.Fields.FirstOrDefault(i => i.Searchable.GetValueOrDefault());
        var fieldName = firstTextField?.Name ?? "Text";

        var queryParser = new StandardQueryParser(analyzer)
        {
            DefaultOperator = Operator.OR,
        };

        return queryParser.Parse(searchText, fieldName);
    }

    private List<string> GetSearchFieldsForQuery(string? searchFields)
    {
        if (string.IsNullOrEmpty(searchFields) || _index == null)
        {
            return [];
        }

        var fields = searchFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return _index.Fields
            .Where(i => i.Searchable.GetValueOrDefault() && fields.Contains(i.Name, StringComparer.OrdinalIgnoreCase))
            .Select(i => i.Name)
            .ToList();
    }

    private Query RestrictQueryToFields(Query query, string searchFields)
    {
        if (_index == null)
        {
            return query;
        }

        var allowedFields = GetSearchFieldsForQuery(searchFields);

        if (allowedFields.Count == 0)
        {
            return query;
        }

        // If the query is already field-specific (e.g., "name:laptop"), keep it
        // Otherwise, wrap it with field restrictions
        if (query is TermQuery termQuery)
        {
            // For term queries, we can apply field restriction more directly
            if (allowedFields.Count == 1)
            {
                return new TermQuery(new Term(allowedFields[0], termQuery.Term.Text));
            }
            else
            {
                // Multiple fields: use BooleanQuery with SHOULD clauses
                var boolQuery = new BooleanQuery();
                foreach (var field in allowedFields)
                {
                    boolQuery.Add(new TermQuery(new Term(field, termQuery.Term.Text)), Occur.SHOULD);
                }
                return boolQuery;
            }
        }

        // For more complex queries, return as-is since they may already have field restrictions
        // The query parser handles field-specific syntax (e.g., "name:laptop")
        return query;
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
}
