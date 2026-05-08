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

    // When walking into a lambda (any/all), pushes the (path, parameter) context so that
    // child RangeVariableTokens can be resolved back to the collection's field path.
    private readonly Stack<LambdaContext> _lambdaContexts = new();

    private record LambdaContext(string Path, string Parameter);

    public Query Visit(AllToken tokenIn) => VisitLambda(tokenIn, isAll: true);

    public Query Visit(AnyToken tokenIn) => VisitLambda(tokenIn, isAll: false);

    private Query VisitLambda(LambdaToken tokenIn, bool isAll)
    {
        var path = ResolveLambdaPath(tokenIn.Parent);
        EnsureFilterable(path);

        // any() with no expression: matches docs where the collection is non-empty.
        // Lucene equivalent: the field must have at least one indexed term.
        if (string.IsNullOrEmpty(tokenIn.Parameter) || tokenIn.Expression is LiteralToken { Value: bool b } && b)
        {
            if (isAll)
            {
                // all() with no body is not meaningful in OData; treat as match-all.
                return new MatchAllDocsQuery();
            }

            // Match docs that have the field present (any indexed value).
            return new ConstantScoreQuery(new WildcardQuery(new Term(path, "*")));
        }

        _lambdaContexts.Push(new LambdaContext(path, tokenIn.Parameter));
        try
        {
            var inner = tokenIn.Expression.Accept(this);

            if (!isAll)
            {
                // any(t: P(t)): the per-value query already matches docs where at least one
                // indexed value satisfies P, since multi-valued fields share a field name.
                return inner;
            }

            // all(t: P(t)) ≡ ¬any(t: ¬P(t)). Implemented as MatchAll MUST_NOT (¬P).
            // We invert P at the leaf-comparison level so that range/term semantics still
            // match the per-value indexing model: e.g. all(t: t ne 'x') becomes
            // "no document has a value equal to 'x'".
            var negated = NegateLambdaExpression(tokenIn.Expression);

            return new BooleanQuery
            {
                Clauses =
                {
                    new BooleanClause(new MatchAllDocsQuery(), Occur.MUST),
                    new BooleanClause(negated, Occur.MUST_NOT)
                }
            };
        }
        finally
        {
            _lambdaContexts.Pop();
        }
    }

    private Query NegateLambdaExpression(QueryToken expression)
    {
        // Logical NOT is rewritten by negating the leaf comparison so that the resulting
        // Lucene query stays a clean "MUST_NOT P" against the multi-valued field, instead
        // of nested boolean wrappers that produce empty result sets.
        return expression switch
        {
            UnaryOperatorToken { OperatorKind: UnaryOperatorKind.Not } unary => unary.Operand.Accept(this),
            BinaryOperatorToken bin => InvertBinary(bin),
            _ => expression.Accept(this)
        };
    }

    private Query InvertBinary(BinaryOperatorToken bin)
    {
        var inverted = bin.OperatorKind switch
        {
            BinaryOperatorKind.Equal => BinaryOperatorKind.NotEqual,
            BinaryOperatorKind.NotEqual => BinaryOperatorKind.Equal,
            BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThanOrEqual,
            BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThan,
            BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThanOrEqual,
            BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThan,
            _ => throw new NotImplementedException($"Cannot invert operator {bin.OperatorKind} inside all(...)")
        };

        var rebuilt = new BinaryOperatorToken(inverted, bin.Left, bin.Right);
        return rebuilt.Accept(this);
    }

    private string ResolveLambdaPath(QueryToken? parent)
    {
        // Build a slash-joined path from chained InnerPathTokens; for our flat schema this
        // is normally just the collection field's name.
        return parent switch
        {
            EndPathToken end => end.Identifier,
            InnerPathToken inner => inner.NextToken == null
                ? inner.Identifier
                : ResolveLambdaPath(inner.NextToken) + "/" + inner.Identifier,
            _ => throw new NotImplementedException("Lambda parent must be a path token")
        };
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

        if (TryResolveComparisonPath(tokenIn.Left, out var path)
            && tokenIn.Right is LiteralToken literalToken)
        {
            EnsureFilterable(path);
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

        // Handle "not field eq value" which OData parses as "(not field) eq value"
        if (tokenIn is
            {
                Left: UnaryOperatorToken { OperatorKind: UnaryOperatorKind.Not, Operand: { } negatedOperand },
                Right: LiteralToken negatedLiteral
            } && TryResolveComparisonPath(negatedOperand, out var negatedPath))
        {
            EnsureFilterable(negatedPath);
            var equalQuery = tokenIn.OperatorKind switch
            {
                BinaryOperatorKind.Equal => HandleEqualComparison(negatedPath, negatedLiteral),
                BinaryOperatorKind.LessThan => HandleLessThanComparison(negatedPath, negatedLiteral),
                BinaryOperatorKind.LessThanOrEqual => HandleLessThanOrEqualComparison(negatedPath, negatedLiteral),
                BinaryOperatorKind.GreaterThan => HandleGreaterThanComparison(negatedPath, negatedLiteral),
                BinaryOperatorKind.GreaterThanOrEqual => HandleGreaterThanOrEqualComparison(negatedPath, negatedLiteral),
                BinaryOperatorKind.NotEqual => HandleNotEqualComparison(negatedPath, negatedLiteral),
                _ => throw new NotImplementedException($"Operator {tokenIn.OperatorKind} not implemented")
            };

            return new BooleanQuery
            {
                Clauses =
                {
                    new BooleanClause(new MatchAllDocsQuery(), Occur.MUST),
                    new BooleanClause(equalQuery, Occur.MUST_NOT)
                }
            };
        }

        throw new NotImplementedException();
    }

    private bool TryResolveComparisonPath(QueryToken token, out string path)
    {
        // A bare end-path is the simple case: "Field eq 'value'".
        if (token is EndPathToken end)
        {
            path = end.Identifier;
            return true;
        }

        // Inside a lambda, the range variable resolves back to the collection's field path.
        if (token is RangeVariableToken rv && _lambdaContexts.Count > 0)
        {
            var ctx = _lambdaContexts.Peek();
            if (string.Equals(rv.Name, ctx.Parameter, StringComparison.Ordinal))
            {
                path = ctx.Path;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private void EnsureFilterable(string path)
    {
        if (_index is null) return;
        var field = _index.Fields.FirstOrDefault(f => string.Equals(f.Name, path, StringComparison.OrdinalIgnoreCase));
        if (field is null) return;
        if (!field.Filterable)
        {
            throw new InvalidOperationException($"Field '{field.Name}' is not filterable.");
        }
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
            float floatValue => NumericRangeQuery.NewDoubleRange(path, (double)floatValue, (double)floatValue, true, true),
            double doubleValue => NumericRangeQuery.NewDoubleRange(path, doubleValue, doubleValue, true, true),
            decimal decimalValue => NumericRangeQuery.NewDoubleRange(path, (double)decimalValue, (double)decimalValue, true, true),
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
            float floatValue => NumericRangeQuery.NewDoubleRange(path, double.NegativeInfinity, (double)floatValue, true, false),
            double doubleValue => NumericRangeQuery.NewDoubleRange(path, double.NegativeInfinity, doubleValue, true, false),
            decimal decimalValue => NumericRangeQuery.NewDoubleRange(path, double.NegativeInfinity, (double)decimalValue, true, false),
            _ => throw new NotImplementedException($"Less than comparison not supported for type {literalToken.Value?.GetType().Name}")
        };
    }

    private static Query HandleLessThanOrEqualComparison(string path, LiteralToken literalToken)
    {
        return literalToken.Value switch
        {
            int intValue => NumericRangeQuery.NewInt32Range(path, int.MinValue, intValue, true, true),
            long longValue => NumericRangeQuery.NewInt64Range(path, long.MinValue, longValue, true, true),
            float floatValue => NumericRangeQuery.NewDoubleRange(path, double.NegativeInfinity, (double)floatValue, true, true),
            double doubleValue => NumericRangeQuery.NewDoubleRange(path, double.NegativeInfinity, doubleValue, true, true),
            decimal decimalValue => NumericRangeQuery.NewDoubleRange(path, double.NegativeInfinity, (double)decimalValue, true, true),
            _ => throw new NotImplementedException($"Less than or equal comparison not supported for type {literalToken.Value?.GetType().Name}")
        };
    }

    private static Query HandleGreaterThanComparison(string path, LiteralToken literalToken)
    {
        return literalToken.Value switch
        {
            int intValue => NumericRangeQuery.NewInt32Range(path, intValue, int.MaxValue, false, true),
            long longValue => NumericRangeQuery.NewInt64Range(path, longValue, long.MaxValue, false, true),
            float floatValue => NumericRangeQuery.NewDoubleRange(path, (double)floatValue, double.PositiveInfinity, false, true),
            double doubleValue => NumericRangeQuery.NewDoubleRange(path, doubleValue, double.PositiveInfinity, false, true),
            decimal decimalValue => NumericRangeQuery.NewDoubleRange(path, (double)decimalValue, double.PositiveInfinity, false, true),
            _ => throw new NotImplementedException($"Greater than comparison not supported for type {literalToken.Value?.GetType().Name}")
        };
    }

    private static Query HandleGreaterThanOrEqualComparison(string path, LiteralToken literalToken)
    {
        return literalToken.Value switch
        {
            int intValue => NumericRangeQuery.NewInt32Range(path, intValue, int.MaxValue, true, true),
            long longValue => NumericRangeQuery.NewInt64Range(path, longValue, long.MaxValue, true, true),
            float floatValue => NumericRangeQuery.NewDoubleRange(path, (double)floatValue, double.PositiveInfinity, true, true),
            double doubleValue => NumericRangeQuery.NewDoubleRange(path, doubleValue, double.PositiveInfinity, true, true),
            decimal decimalValue => NumericRangeQuery.NewDoubleRange(path, (double)decimalValue, double.PositiveInfinity, true, true),
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
        if (TryResolveComparisonPath(tokenIn.Left, out var path)
            && tokenIn.Right is LiteralToken { Value: string valueString })
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

    private BooleanQuery VisitSearchIn(FunctionCallToken tokenIn)
    {
        var args = tokenIn.Arguments.ToList();

        if (args.Count is < 2 or > 3)
        {
            throw new ArgumentException("search.in requires two or three arguments");
        }

        if (!TryResolveComparisonPath(args[0].ValueToken, out var path))
        {
            throw new NotImplementedException("Passing anything other than an end path or lambda variable as the first parameter to search.in is not yet implemented");
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
