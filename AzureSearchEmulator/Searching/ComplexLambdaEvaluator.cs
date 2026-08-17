using System.Text;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using Lucene.Net.Index;
using Lucene.Net.Util;
using Microsoft.OData.UriParser;
using Microsoft.Spatial;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Compiles the body of an <c>any</c>/<c>all</c> lambda over a
/// <c>Collection(Edm.ComplexType)</c> into a predicate evaluated against one element at a
/// time.
/// </summary>
/// <remarks>
/// Azure Search evaluates such a lambda per element, so that every criterion inside it
/// applies to the <em>same</em> element: <c>Rooms/any(r: r/Type eq 'Deluxe' and r/BaseRate lt
/// 100)</c> matches only a hotel with one room that is both. Flattened leaf fields cannot
/// express that, because they lose which value belonged to which element — a document with an
/// expensive deluxe room and a separate cheap room would match. See
/// https://learn.microsoft.com/en-us/azure/search/search-query-understand-collection-filters#correlated-versus-uncorrelated-search
///
/// The elements are therefore evaluated as materialized objects rather than through the
/// inverted index, which is also why the operators Azure restricts over primitive collections
/// are all available here.
/// </remarks>
public static class ComplexLambdaEvaluator
{
    /// <summary>
    /// Reads the elements of a complex collection for a document, as written by
    /// <see cref="ComplexTypeSupport.GetComplexElementsDocValuesFieldName"/>.
    /// </summary>
    /// <remarks>
    /// Returns an empty list when the field is absent, which gives an empty collection the
    /// OData semantics: <c>any</c> is false over it, and <c>all</c> is vacuously true.
    /// </remarks>
    public static Func<int, IReadOnlyList<JsonObject>> GetElementReader(AtomicReader reader, string path)
    {
        var values = reader.GetBinaryDocValues(ComplexTypeSupport.GetComplexElementsDocValuesFieldName(path));

        if (values is null)
        {
            return _ => [];
        }

        // Filters revisit the same document across clauses, and parsing dominates the cost
        // here, so parsed elements are memoized for the lifetime of this segment's reader.
        var cache = new Dictionary<int, IReadOnlyList<JsonObject>>();
        var term = new BytesRef();

        return doc =>
        {
            if (cache.TryGetValue(doc, out var cached))
            {
                return cached;
            }

            values.Get(doc, term);

            var elements = ParseElements(term);

            cache[doc] = elements;

            return elements;
        };
    }

    private static IReadOnlyList<JsonObject> ParseElements(BytesRef term)
    {
        if (term.Length == 0)
        {
            return [];
        }

        var json = Encoding.UTF8.GetString(term.Bytes, term.Offset, term.Length);

        if (JsonNode.Parse(json) is not JsonArray array)
        {
            return [];
        }

        return array.OfType<JsonObject>().ToList();
    }

    /// <summary>
    /// Compiles <paramref name="expression"/> into a predicate over a single element.
    /// </summary>
    /// <param name="expression">The lambda body.</param>
    /// <param name="parameter">The lambda's range variable, i.e. <c>r</c> in <c>r/Type</c>.</param>
    /// <param name="elementField">The complex collection whose elements are being tested.</param>
    /// <param name="collectionPath">The collection's full path, used in error messages.</param>
    public static Func<JsonObject, bool> Compile(
        QueryToken expression,
        string parameter,
        SearchField elementField,
        string collectionPath)
    {
        return new Compiler(parameter, elementField, collectionPath).Compile(expression);
    }

    private sealed class Compiler(string parameter, SearchField elementField, string collectionPath)
    {
        public Func<JsonObject, bool> Compile(QueryToken expression)
        {
            switch (expression)
            {
                case BinaryOperatorToken { OperatorKind: BinaryOperatorKind.And } and:
                {
                    var left = Compile(and.Left);
                    var right = Compile(and.Right);
                    return e => left(e) && right(e);
                }

                case BinaryOperatorToken { OperatorKind: BinaryOperatorKind.Or } or:
                {
                    var left = Compile(or.Left);
                    var right = Compile(or.Right);
                    return e => left(e) || right(e);
                }

                case UnaryOperatorToken { OperatorKind: UnaryOperatorKind.Not } not:
                {
                    var operand = Compile(not.Operand);
                    return e => !operand(e);
                }

                case BinaryOperatorToken binary:
                    return CompileComparison(binary);

                case LambdaToken lambda:
                    return CompileNestedLambda(lambda);

                case FunctionCallToken function:
                    return CompileFunction(function);

                // A bare boolean sub-field used as a predicate, i.e.
                // "Rooms/all(r: not r/SmokingAllowed)".
                case EndPathToken path:
                {
                    var elementPath = ResolveElementPath(path);
                    return e => GetValue(e, elementPath) is JsonValue v
                                && v.TryGetValue<bool>(out var b)
                                && b;
                }

                case LiteralToken { Value: bool literal }:
                    return _ => literal;

                default:
                    throw new NotImplementedException(
                        $"Expression of type {expression.GetType().Name} is not supported inside a lambda over '{collectionPath}'.");
            }
        }

        private Func<JsonObject, bool> CompileComparison(BinaryOperatorToken binary)
        {
            // geo.distance(...) is a comparison rather than a standalone function call.
            if (TryCompileGeoDistance(binary, out var geoDistance))
            {
                return geoDistance;
            }

            var (pathToken, literal, operatorKind) = binary switch
            {
                { Left: EndPathToken p, Right: LiteralToken l } => (p, l, binary.OperatorKind),
                { Right: EndPathToken p, Left: LiteralToken l } => (p, l, Flip(binary.OperatorKind)),
                _ => throw new NotImplementedException(
                    $"Only 'field op literal' comparisons are supported inside a lambda over '{collectionPath}'.")
            };

            var elementPath = ResolveElementPath(pathToken);
            var expected = literal.Value;

            return e => Compare(GetValue(e, elementPath), expected, operatorKind);
        }

        private Func<JsonObject, bool> CompileNestedLambda(LambdaToken lambda)
        {
            var isAll = lambda is AllToken;
            var nestedPath = ResolveElementPath(lambda.Parent
                ?? throw new NotImplementedException("Lambda parent must be a path token"));

            var nestedField = ComplexTypeSupport.FindFieldByPath(
                new SearchIndex { Fields = elementField.Fields }, nestedPath);

            // any() with no body tests the nested collection for elements.
            if (string.IsNullOrEmpty(lambda.Parameter))
            {
                return e => GetValue(e, nestedPath) is JsonArray a && (isAll || a.Count > 0);
            }

            if (nestedField is not null && nestedField.IsComplexCollection())
            {
                // A complex collection inside a complex collection: recurse into the nested
                // element objects, which live inside the value already materialized here.
                var inner = new Compiler(lambda.Parameter, nestedField, nestedPath)
                    .Compile(lambda.Expression);

                return e =>
                {
                    var nested = (GetValue(e, nestedPath) as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                    return isAll ? nested.All(inner) : nested.Any(inner);
                };
            }

            // A primitive collection, i.e. Rooms/any(r: r/Tags/any(t: t eq 'wifi')). The
            // range variable stands for a scalar rather than an object, so the body is
            // compiled against a synthetic single-property wrapper.
            var scalar = CompileScalarPredicate(lambda.Expression, lambda.Parameter, nestedPath);

            return e =>
            {
                var nested = (GetValue(e, nestedPath) as JsonArray)?.ToList() ?? [];
                return isAll ? nested.All(scalar) : nested.Any(scalar);
            };
        }

        /// <summary>
        /// Compiles a lambda body whose range variable is a scalar rather than an object,
        /// as in <c>r/Tags/any(t: t eq 'wifi')</c>.
        /// </summary>
        private Func<JsonNode?, bool> CompileScalarPredicate(QueryToken expression, string scalarParameter, string nestedPath)
        {
            switch (expression)
            {
                case BinaryOperatorToken { OperatorKind: BinaryOperatorKind.And } and:
                {
                    var left = CompileScalarPredicate(and.Left, scalarParameter, nestedPath);
                    var right = CompileScalarPredicate(and.Right, scalarParameter, nestedPath);
                    return v => left(v) && right(v);
                }

                case BinaryOperatorToken { OperatorKind: BinaryOperatorKind.Or } or:
                {
                    var left = CompileScalarPredicate(or.Left, scalarParameter, nestedPath);
                    var right = CompileScalarPredicate(or.Right, scalarParameter, nestedPath);
                    return v => left(v) || right(v);
                }

                case UnaryOperatorToken { OperatorKind: UnaryOperatorKind.Not } not:
                {
                    var operand = CompileScalarPredicate(not.Operand, scalarParameter, nestedPath);
                    return v => !operand(v);
                }

                case BinaryOperatorToken binary:
                {
                    var (isRangeVariable, literal, operatorKind) = binary switch
                    {
                        { Left: RangeVariableToken r, Right: LiteralToken l } =>
                            (r.Name == scalarParameter, l, binary.OperatorKind),
                        { Right: RangeVariableToken r, Left: LiteralToken l } =>
                            (r.Name == scalarParameter, l, Flip(binary.OperatorKind)),
                        _ => (false, null, binary.OperatorKind)
                    };

                    if (!isRangeVariable || literal is null)
                    {
                        throw new NotImplementedException(
                            $"Only comparisons against the range variable are supported inside the lambda over '{nestedPath}'.");
                    }

                    var expected = literal.Value;
                    return v => Compare(v, expected, operatorKind);
                }

                case FunctionCallToken { Name: "search.in" } searchIn:
                {
                    var values = GetSearchInValues(searchIn);
                    return v => v is JsonValue jv
                                && jv.TryGetValue<string>(out var s)
                                && values.Contains(s);
                }

                case RangeVariableToken range when range.Name == scalarParameter:
                    return v => v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;

                default:
                    throw new NotImplementedException(
                        $"Expression of type {expression.GetType().Name} is not supported inside the lambda over '{nestedPath}'.");
            }
        }

        private Func<JsonObject, bool> CompileFunction(FunctionCallToken function)
        {
            switch (function.Name)
            {
                case "search.in":
                {
                    var args = function.Arguments.ToList();

                    if (args.Count < 2 || args[0].ValueToken is not EndPathToken pathToken)
                    {
                        throw new NotImplementedException(
                            "search.in requires a field path as its first argument.");
                    }

                    var elementPath = ResolveElementPath(pathToken);
                    var values = GetSearchInValues(function);

                    return e => GetValue(e, elementPath) is JsonValue v
                                && v.TryGetValue<string>(out var s)
                                && values.Contains(s);
                }

                case "geo.intersects":
                {
                    var args = function.Arguments.ToList();

                    if (args.Count != 2
                        || args[0].ValueToken is not EndPathToken pathToken
                        || args[1].ValueToken is not LiteralToken { Value: GeographyPolygon polygon })
                    {
                        throw new NotImplementedException(
                            "geo.intersects requires a field path and a polygon constant.");
                    }

                    var elementPath = ResolveElementPath(pathToken);
                    var ring = GeoSupport.ValidateRing(
                        polygon.Rings[0].Points.Select(p => (p.Longitude, p.Latitude)).ToList());

                    return e => TryGetPoint(e, elementPath, out var point)
                                && GeoSupport.IsPointInPolygon(ring, point.Lon, point.Lat);
                }

                // Excluded by Azure Search inside any lambda expression, because full-text
                // matching has no notion of a "current element" to bind the range variable to.
                // https://learn.microsoft.com/en-us/azure/search/search-query-troubleshoot-collection-filters#rules-for-filtering-complex-collections
                case "search.ismatch":
                case "search.ismatchscoring":
                    throw new InvalidOperationException(
                        $"The function '{function.Name}' is not supported inside a lambda expression. "
                        + "Move it outside the lambda and combine the two with 'and'.");

                default:
                    throw new NotImplementedException($"Function {function.Name} is not supported inside a lambda.");
            }
        }

        private bool TryCompileGeoDistance(BinaryOperatorToken binary, out Func<JsonObject, bool> predicate)
        {
            predicate = null!;

            var (function, distanceToken, operatorKind) = binary switch
            {
                { Left: FunctionCallToken { Name: "geo.distance" } f, Right: LiteralToken l } =>
                    (f, l, binary.OperatorKind),
                { Right: FunctionCallToken { Name: "geo.distance" } f, Left: LiteralToken l } =>
                    (f, l, Flip(binary.OperatorKind)),
                _ => (null, null, binary.OperatorKind)
            };

            if (function is null || distanceToken is null)
            {
                return false;
            }

            var args = function.Arguments.ToList();

            if (args.Count != 2
                || args[0].ValueToken is not EndPathToken pathToken
                || args[1].ValueToken is not LiteralToken { Value: GeographyPoint origin })
            {
                throw new NotImplementedException(
                    "geo.distance requires a field path and a geography point constant.");
            }

            var elementPath = ResolveElementPath(pathToken);

            if (!TryGetDouble(distanceToken.Value, out var distanceKm))
            {
                throw new InvalidOperationException(
                    "geo.distance must be compared to a numeric distance in kilometers.");
            }

            predicate = e =>
            {
                if (!TryGetPoint(e, elementPath, out var point))
                {
                    // A null point fails every comparison, as it does outside a lambda.
                    return false;
                }

                var actual = GeoSupport.GetDistanceKm(point.Lon, point.Lat, origin.Longitude, origin.Latitude);

                return operatorKind switch
                {
                    BinaryOperatorKind.LessThan => actual < distanceKm,
                    BinaryOperatorKind.LessThanOrEqual => actual <= distanceKm,
                    BinaryOperatorKind.GreaterThan => actual > distanceKm,
                    BinaryOperatorKind.GreaterThanOrEqual => actual >= distanceKm,
                    _ => throw new InvalidOperationException(
                        $"Operator {operatorKind} is not supported for geo.distance; use lt, le, gt, or ge.")
                };
            };

            return true;
        }

        private static HashSet<string> GetSearchInValues(FunctionCallToken function)
        {
            var args = function.Arguments.ToList();

            if (args.Count is < 2 or > 3 || args[1].ValueToken is not LiteralToken { Value: string list })
            {
                throw new NotImplementedException("search.in requires a string list argument.");
            }

            var delimiters = new[] { ',', ' ' };

            if (args.Count == 3 && args[2].ValueToken is LiteralToken { Value: string custom })
            {
                delimiters = custom.ToCharArray();
            }

            return new HashSet<string>(
                list.Split(delimiters, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);
        }

        /// <summary>
        /// Resolves a path token to a path relative to the element, i.e. <c>Type</c> for
        /// <c>r/Type</c> or <c>Geo/Lat</c> for <c>r/Geo/Lat</c>.
        /// </summary>
        /// <remarks>
        /// A path whose root is not this lambda's range variable is a <em>free variable</em>,
        /// which Azure Search rejects: a lambda body may only reference fields bound to its
        /// own range variable, so a filter mixing the two has to lift the unbound clause out
        /// of the lambda.
        /// https://learn.microsoft.com/en-us/azure/search/search-query-troubleshoot-collection-filters#rules-for-filtering-complex-collections
        /// </remarks>
        private string ResolveElementPath(QueryToken token)
        {
            var segments = new List<string>();
            var current = token;

            while (true)
            {
                switch (current)
                {
                    case EndPathToken end:
                        segments.Insert(0, end.Identifier);
                        if (end.NextToken is null)
                        {
                            throw FreeVariable(string.Join('/', segments));
                        }
                        current = end.NextToken;
                        continue;

                    case InnerPathToken inner:
                        segments.Insert(0, inner.Identifier);
                        if (inner.NextToken is null)
                        {
                            throw FreeVariable(string.Join('/', segments));
                        }
                        current = inner.NextToken;
                        continue;

                    case RangeVariableToken range when range.Name == parameter:
                        return string.Join('/', segments);

                    case RangeVariableToken range:
                        throw FreeVariable(range.Name);

                    default:
                        throw new NotImplementedException(
                            $"Unable to resolve a field path from a {current.GetType().Name} inside a lambda.");
                }
            }
        }

        private InvalidOperationException FreeVariable(string name) => new(
            $"'{name}' is not bound to the range variable '{parameter}' of the lambda over "
            + $"'{collectionPath}'. Only bound field references are supported inside a lambda; "
            + "move the unbound comparison outside it.");

        private static JsonNode? GetValue(JsonObject element, string elementPath)
        {
            JsonNode? current = element;

            foreach (var segment in elementPath.Split(ComplexTypeSupport.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (current is not JsonObject obj)
                {
                    return null;
                }

                current = obj.FirstOrDefault(p =>
                    string.Equals(p.Key, segment, StringComparison.OrdinalIgnoreCase)).Value;
            }

            return current;
        }

        private static bool TryGetPoint(JsonObject element, string elementPath, out (double Lon, double Lat) point)
        {
            point = default;

            if (GetValue(element, elementPath) is not JsonNode node)
            {
                return false;
            }

            try
            {
                point = GeoSupport.ParseGeoJsonPoint(elementPath, node);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Compares an element's value against a literal.
        /// </summary>
        /// <remarks>
        /// A null or absent value fails every comparison against a literal value, including
        /// <c>ne</c>, matching how Azure Search treats nulls in filters. It satisfies only
        /// <c>eq null</c>, which is the one comparison that asks about absence itself.
        /// Numbers are compared as doubles so that a literal written as an integer still
        /// matches an <c>Edm.Double</c> sub-field.
        /// </remarks>
        private static bool Compare(JsonNode? actual, object? expected, BinaryOperatorKind operatorKind)
        {
            if (actual is not JsonValue value)
            {
                // An absent sub-field, or one explicitly written as JSON null, is null.
                return expected is null && operatorKind == BinaryOperatorKind.Equal;
            }

            if (expected is string expectedString)
            {
                if (!value.TryGetValue<string>(out var actualString))
                {
                    return false;
                }

                // Ordinal ordering, matching the byte-wise term ordering the flattened
                // Lucene path uses for string ranges, so both paths agree on 'Z' < 'a'.
                return Satisfies(
                    string.CompareOrdinal(actualString, expectedString),
                    operatorKind);
            }

            if (expected is bool expectedBool)
            {
                if (!value.TryGetValue<bool>(out var actualBool))
                {
                    return false;
                }

                return operatorKind switch
                {
                    BinaryOperatorKind.Equal => actualBool == expectedBool,
                    BinaryOperatorKind.NotEqual => actualBool != expectedBool,
                    _ => throw new InvalidOperationException(
                        $"Operator {operatorKind} is not supported for boolean comparisons.")
                };
            }

            if (expected is DateTimeOffset expectedDate)
            {
                if (!value.TryGetValue<DateTimeOffset>(out var actualDate))
                {
                    return false;
                }

                var comparison = actualDate.CompareTo(expectedDate);
                return Satisfies(comparison, operatorKind);
            }

            if (TryGetDouble(expected, out var expectedNumber))
            {
                if (!value.TryGetValue<double>(out var actualNumber))
                {
                    return false;
                }

                return Satisfies(actualNumber.CompareTo(expectedNumber), operatorKind);
            }

            // A comparison against null: only "eq null" / "ne null" are meaningful, and the
            // value is known to be non-null here.
            return expected is null && operatorKind == BinaryOperatorKind.NotEqual;
        }

        private static bool Satisfies(int comparison, BinaryOperatorKind operatorKind) => operatorKind switch
        {
            BinaryOperatorKind.Equal => comparison == 0,
            BinaryOperatorKind.NotEqual => comparison != 0,
            BinaryOperatorKind.LessThan => comparison < 0,
            BinaryOperatorKind.LessThanOrEqual => comparison <= 0,
            BinaryOperatorKind.GreaterThan => comparison > 0,
            BinaryOperatorKind.GreaterThanOrEqual => comparison >= 0,
            _ => throw new InvalidOperationException($"Operator {operatorKind} is not supported in a comparison.")
        };

        private static BinaryOperatorKind Flip(BinaryOperatorKind kind) => kind switch
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
    }
}
