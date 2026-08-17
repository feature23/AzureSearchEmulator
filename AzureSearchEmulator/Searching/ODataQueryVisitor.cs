using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Analysis;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Flexible.Standard;
using Lucene.Net.QueryParsers.Simple;
using Lucene.Net.Search;
using Microsoft.OData.UriParser;
using Microsoft.OData.UriParser.Aggregation;
using Microsoft.Spatial;
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

        var lambdaField = _index is null ? null : ComplexTypeSupport.FindFieldByPath(_index, path);
        var isComplexCollection = lambdaField?.IsComplexCollection() == true;

        // any() with no expression: matches docs where the collection is non-empty.
        // Lucene equivalent: the field must have at least one indexed term.
        if (string.IsNullOrEmpty(tokenIn.Parameter) || tokenIn.Expression is LiteralToken { Value: bool b } && b)
        {
            if (isAll)
            {
                // all() with no body is not meaningful in OData; treat as match-all.
                return new MatchAllDocsQuery();
            }

            // A complex collection is tested against its stored elements rather than its
            // indexed leaves: an element whose sub-fields are all null indexes no term at
            // all, so a leaf-presence test would report a non-empty collection as empty.
            if (isComplexCollection)
            {
                return new ComplexCollectionLambdaQuery(
                    path, isAll: false, _ => true, candidatePrefilter: null, $"{path}/any()");
            }

            // Match docs that have the field present (any indexed value).
            return new ConstantScoreQuery(new WildcardQuery(new Term(path, "*")));
        }

        // A lambda over a complex collection is evaluated per element, so that every
        // criterion inside it applies to the same element. The flattened leaf fields cannot
        // express that correlation — see ComplexLambdaEvaluator.
        if (isComplexCollection)
        {
            return VisitComplexCollectionLambda(tokenIn, isAll, path, lambdaField!);
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

    /// <summary>
    /// Builds the per-element query for a lambda over a <c>Collection(Edm.ComplexType)</c>.
    /// </summary>
    /// <remarks>
    /// For <c>any</c>, the flattened translation of the body is reused as a candidate
    /// prefilter: it ignores which element each value came from, so it matches a superset of
    /// the correct answer and can only ever be narrowed by the exact per-element test. It is
    /// skipped when the body contains a construct the flattened path cannot translate, since
    /// a prefilter is only an optimization.
    /// </remarks>
    private Query VisitComplexCollectionLambda(LambdaToken tokenIn, bool isAll, string path, SearchField field)
    {
        var predicate = ComplexLambdaEvaluator.Compile(tokenIn.Expression, tokenIn.Parameter, field, path);

        Query? prefilter = null;

        if (!isAll)
        {
            _lambdaContexts.Push(new LambdaContext(path, tokenIn.Parameter));
            try
            {
                prefilter = tokenIn.Expression.Accept(this);
            }
            catch (Exception)
            {
                // The body uses something the flattened translation does not handle. The
                // exact evaluation below still covers it, so the query runs unprefiltered.
                prefilter = null;
            }
            finally
            {
                _lambdaContexts.Pop();
            }
        }

        var description = $"{path}/{(isAll ? "all" : "any")}({tokenIn.Parameter}: ...)";

        return new ComplexCollectionLambdaQuery(path, isAll, predicate, prefilter, description);
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
        // Equality is inverted here directly rather than by routing through NotEqual.
        // HandleNotEqualComparison wraps its result in its own MUST_NOT, and VisitLambda
        // negates again on the way out, so the two cancel and all(...) would silently
        // degrade into any(...).
        if (bin.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual)
        {
            if (!TryResolveComparisonPath(bin.Left, out var equalPath) || bin.Right is not LiteralToken equalLiteral)
            {
                throw new NotImplementedException("Only 'field op literal' comparisons are supported inside all(...)");
            }

            EnsureFilterable(equalPath);

            // The caller wraps whatever comes back in MUST_NOT. For "all(t: t ne 'x')" the
            // predicate to exclude is "some value is 'x'", which is exactly the equality
            // query, so it is returned un-negated.
            if (bin.OperatorKind == BinaryOperatorKind.NotEqual)
            {
                return HandleEqualComparison(equalPath, CoerceLiteralToFieldType(equalPath, equalLiteral));
            }

            // "all(t: t eq 'x')" needs "every value is 'x'", which over a multi-valued field
            // means excluding documents that hold some *other* value — a question the
            // inverted index cannot answer directly. It is only decidable where values are
            // correlated per element, which is why Azure Search allows eq inside all(...)
            // for Collection(Edm.ComplexType) but not for Collection(Edm.String).
            throw new NotImplementedException(
                "all(...) with 'eq' requires per-element correlation.");
        }

        var inverted = bin.OperatorKind switch
        {
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
        // The collection being iterated, i.e. "Rooms" in "Rooms/any(...)" or the nested
        // "Rooms/Tags" in "Rooms/any(r: r/Tags/any(t: t eq 'wifi'))".
        if (parent is null)
        {
            throw new NotImplementedException("Lambda parent must be a path token");
        }

        return CanonicalizePath(ResolvePathPrefix(parent));
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

        // geo.distance(...) is only meaningful when compared to a distance, so it is matched
        // here as part of the comparison rather than in Visit(FunctionCallToken).
        if (TryVisitGeoDistanceComparison(tokenIn, out var geoDistanceQuery))
        {
            return geoDistanceQuery;
        }

        if (TryResolveComparisonPath(tokenIn.Left, out var path)
            && tokenIn.Right is LiteralToken literalToken)
        {
            EnsureFilterable(path);
            literalToken = CoerceLiteralToFieldType(path, literalToken);
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
            negatedLiteral = CoerceLiteralToFieldType(negatedPath, negatedLiteral);
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
        // A bare end-path is the simple case: "Field eq 'value'". Inside a complex type it
        // arrives as a chain, i.e. "Address/City", which NextToken walks up.
        if (token is EndPathToken end)
        {
            path = CanonicalizePath(end.NextToken is null
                ? end.Identifier
                : ResolvePathPrefix(end.NextToken) + ComplexTypeSupport.PathSeparator + end.Identifier);
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

    /// <summary>
    /// Resolves the portion of a path that precedes a leaf, i.e. the <c>Address</c> of
    /// <c>Address/City</c>, or the collection path a lambda's range variable stands for in
    /// <c>Rooms/any(r: r/Type eq 'Deluxe')</c>.
    /// </summary>
    private string ResolvePathPrefix(QueryToken token)
    {
        switch (token)
        {
            case RangeVariableToken rv when _lambdaContexts.Count > 0:
            {
                // "r" in "r/Type" stands for an element of the collection, which is indexed
                // under the collection's own path, so "r/Type" becomes "Rooms/Type".
                var ctx = _lambdaContexts.FirstOrDefault(c =>
                    string.Equals(rv.Name, c.Parameter, StringComparison.Ordinal));

                if (ctx is not null)
                {
                    return ctx.Path;
                }

                break;
            }

            case InnerPathToken inner:
                return inner.NextToken is null
                    ? inner.Identifier
                    : ResolvePathPrefix(inner.NextToken) + ComplexTypeSupport.PathSeparator + inner.Identifier;

            case EndPathToken end:
                return end.NextToken is null
                    ? end.Identifier
                    : ResolvePathPrefix(end.NextToken) + ComplexTypeSupport.PathSeparator + end.Identifier;
        }

        throw new NotImplementedException($"Unable to resolve field path from a {token.GetType().Name}.");
    }

    private void EnsureFilterable(string path)
    {
        if (_index is null) return;
        // Sub-fields of a complex type are addressed by their slash-delimited path.
        var field = ComplexTypeSupport.FindFieldByPath(_index, path);
        if (field is null) return;
        if (!field.Filterable)
        {
            throw new InvalidOperationException($"Field '{path}' is not filterable.");
        }
    }

    /// <summary>
    /// Rewrites a field path to the casing the schema declares, since a filter may name a
    /// field in any casing but Lucene field names are case-sensitive.
    /// </summary>
    private string CanonicalizePath(string path)
        => _index is not null && ComplexTypeSupport.TryResolvePath(_index, path, out _, out var canonical)
            ? canonical
            : path;

    private static Occur GetOccurFromOperator(BinaryOperatorKind operatorKind)
    {
        return operatorKind switch
        {
            BinaryOperatorKind.Or => Occur.SHOULD,
            BinaryOperatorKind.And => Occur.MUST,
            _ => throw new NotImplementedException()
        };
    }

    /// <summary>
    /// Rewrites a numeric literal to the CLR type matching the field's declared Edm type.
    /// </summary>
    /// <remarks>
    /// Lucene encodes each numeric width differently, so a range query has to be built from
    /// the same width the values were indexed with. The OData parser types a literal from
    /// its written form alone — <c>100</c> arrives as an <see cref="int"/> — so comparing it
    /// against an <c>Edm.Double</c> field would otherwise build an Int32 range that silently
    /// matches nothing.
    /// </remarks>
    private LiteralToken CoerceLiteralToFieldType(string path, LiteralToken literalToken)
    {
        if (_index is null)
        {
            return literalToken;
        }

        var field = ComplexTypeSupport.FindFieldByPath(_index, path);

        if (field is null)
        {
            return literalToken;
        }

        var type = field.IsCollection() ? SearchFieldExtensions.GetCollectionElementType(field.Type) : field.Type;

        // Only widen numerics; strings, booleans and dates already line up with how they
        // were indexed.
        if (!TryGetDouble(literalToken.Value, out var numeric))
        {
            return literalToken;
        }

        object? coerced = type switch
        {
            "Edm.Double" => numeric,
            "Edm.Int64" => (long)numeric,
            "Edm.Int32" when numeric is >= int.MinValue and <= int.MaxValue => (int)numeric,
            _ => null
        };

        return coerced is null || coerced.Equals(literalToken.Value)
            ? literalToken
            : new LiteralToken(coerced);
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
            "geo.intersects" => VisitGeoIntersects(tokenIn),
            "geo.distance" => throw new InvalidOperationException(
                "geo.distance must be compared to a distance in kilometers using lt, le, gt, or ge."),
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

    /// <summary>
    /// Handles <c>geo.distance(field, geography'POINT(lon lat)') le 10</c> and its variants.
    /// </summary>
    private bool TryVisitGeoDistanceComparison(BinaryOperatorToken tokenIn, out Query query)
    {
        query = null!;

        // The function may sit on either side of the comparison; if it's on the right,
        // the operator has to be flipped along with it ("10 ge geo.distance(...)").
        var (functionToken, distanceToken, operatorKind) = tokenIn switch
        {
            { Left: FunctionCallToken { Name: "geo.distance" } fn, Right: LiteralToken lit } =>
                (fn, lit, tokenIn.OperatorKind),
            { Right: FunctionCallToken { Name: "geo.distance" } fn, Left: LiteralToken lit } =>
                (fn, lit, FlipComparison(tokenIn.OperatorKind)),
            _ => (null, null, tokenIn.OperatorKind)
        };

        if (functionToken is null || distanceToken is null)
        {
            return false;
        }

        var (path, origin) = ParseGeoFunctionArguments(functionToken, "geo.distance");

        EnsureFilterable(path);
        EnsureGeographyPoint(path, "geo.distance");

        if (!TryGetDouble(distanceToken.Value, out var distanceKm))
        {
            throw new InvalidOperationException("geo.distance must be compared to a numeric distance in kilometers.");
        }

        var (withinDistance, inclusive) = operatorKind switch
        {
            BinaryOperatorKind.LessThan => (true, false),
            BinaryOperatorKind.LessThanOrEqual => (true, true),
            BinaryOperatorKind.GreaterThan => (false, false),
            BinaryOperatorKind.GreaterThanOrEqual => (false, true),
            // Azure Search rejects eq/ne against a distance, since exact floating point
            // equality on a computed distance is not meaningful.
            _ => throw new InvalidOperationException(
                $"Operator {operatorKind} is not supported for geo.distance; use lt, le, gt, or ge.")
        };

        query = new GeoDistanceQuery(path, origin.Lon, origin.Lat, distanceKm, withinDistance, inclusive);

        return true;
    }

    private Query VisitGeoIntersects(FunctionCallToken tokenIn)
    {
        var args = tokenIn.Arguments.ToList();

        if (args.Count != 2)
        {
            throw new ArgumentException("geo.intersects requires two arguments");
        }

        if (!TryResolveComparisonPath(args[0].ValueToken, out var path))
        {
            throw new InvalidOperationException("The first argument to geo.intersects must be a field path.");
        }

        if (args[1].ValueToken is not LiteralToken polygonLiteral)
        {
            throw new InvalidOperationException("The second argument to geo.intersects must be a polygon literal.");
        }

        EnsureFilterable(path);
        EnsureGeographyPoint(path, "geo.intersects");

        var ring = GetRingFromLiteral(polygonLiteral);

        return new GeoIntersectsQuery(path, ring);
    }

    /// <summary>
    /// Resolves the (field path, point) argument pair shared by the geo functions. Azure
    /// Search allows the field and the constant in either order.
    /// </summary>
    private (string Path, (double Lon, double Lat) Point) ParseGeoFunctionArguments(
        FunctionCallToken tokenIn,
        string functionName)
    {
        var args = tokenIn.Arguments.ToList();

        if (args.Count != 2)
        {
            throw new ArgumentException($"{functionName} requires two arguments");
        }

        if (TryResolveComparisonPath(args[0].ValueToken, out var path)
            && args[1].ValueToken is LiteralToken pointLiteral)
        {
            return (path, GetPointFromLiteral(pointLiteral));
        }

        if (TryResolveComparisonPath(args[1].ValueToken, out path)
            && args[0].ValueToken is LiteralToken reversedLiteral)
        {
            return (path, GetPointFromLiteral(reversedLiteral));
        }

        throw new InvalidOperationException(
            $"{functionName} requires one field path argument and one geography point constant.");
    }

    /// <summary>
    /// Reads a <c>geography'POINT(lon lat)'</c> literal.
    /// </summary>
    /// <remarks>
    /// The OData parser resolves geography literals into Microsoft.Spatial shapes before we
    /// see them, so the coordinates are read from the parsed value rather than by
    /// re-parsing the WKT text.
    /// </remarks>
    private static (double Lon, double Lat) GetPointFromLiteral(LiteralToken literal)
    {
        if (literal.Value is not GeographyPoint point)
        {
            throw new InvalidOperationException(
                "Expected a geography point constant, i.e. geography'POINT(longitude latitude)'.");
        }

        return (point.Longitude, point.Latitude);
    }

    /// <summary>
    /// Reads the bounding ring of a <c>geography'POLYGON((...))'</c> literal.
    /// </summary>
    private static IReadOnlyList<(double Lon, double Lat)> GetRingFromLiteral(LiteralToken literal)
    {
        if (literal.Value is not GeographyPolygon polygon)
        {
            throw new InvalidOperationException(
                "Expected a geography polygon constant, i.e. geography'POLYGON((lon lat, ...))'.");
        }

        if (polygon.Rings.Count != 1)
        {
            throw new InvalidOperationException(
                $"geo.intersects requires a polygon with exactly one bounding ring, but got {polygon.Rings.Count}.");
        }

        var ring = polygon.Rings[0].Points.Select(i => (i.Longitude, i.Latitude)).ToList();

        return GeoSupport.ValidateRing(ring);
    }

    private static BinaryOperatorKind FlipComparison(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
        BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
        BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
        BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
        _ => kind
    };

    private static bool TryGetDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d: result = d; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case float f: result = f; return true;
            case decimal m: result = (double)m; return true;
            default: result = 0; return false;
        }
    }

    private void EnsureGeographyPoint(string path, string functionName)
    {
        if (_index is null) return;

        var field = ComplexTypeSupport.FindFieldByPath(_index, path);

        if (field is null) return;

        // Collection(Edm.GeographyPoint) is allowed as well: Azure evaluates the geo functions
        // against a range variable iterating the collection, and the filters match a document
        // when any of its points satisfies the predicate.
        if (field.Type != GeoSupport.GeographyPointType
            && field.Type != $"Collection({GeoSupport.GeographyPointType})")
        {
            throw new InvalidOperationException(
                $"Field '{field.Name}' is of type {field.Type}; {functionName} requires a {GeoSupport.GeographyPointType} field.");
        }
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
            // If no specific fields, use all searchable fields, including the searchable
            // sub-fields of complex types under their full paths.
            fieldsToSearch = ComplexTypeSupport.EnumerateLeafFields(_index!)
                .Where(i => i.Field.Searchable.GetValueOrDefault())
                .Select(i => i.Path)
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

        return ComplexTypeSupport.EnumerateLeafFields(_index)
            .Where(i => i.Field.Searchable.GetValueOrDefault()
                        && fields.Contains(i.Path, StringComparer.OrdinalIgnoreCase))
            .Select(i => i.Path)
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
