using System.Text.Json.Nodes;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Applies an <c>any</c>/<c>all</c> lambda predicate to the elements of a
/// <c>Collection(Edm.ComplexType)</c>, one document at a time.
/// </summary>
/// <remarks>
/// An empty collection follows the OData rules: <c>any</c> is false over it, while <c>all</c>
/// is vacuously true. A document that never had the field behaves the same as one with an
/// empty array.
/// </remarks>
public class ComplexCollectionLambdaFilter(string path, bool isAll, Func<JsonObject, bool> predicate) : Filter
{
    public override DocIdSet GetDocIdSet(AtomicReaderContext context, IBits? acceptDocs)
    {
        var reader = context.AtomicReader;
        var readElements = ComplexLambdaEvaluator.GetElementReader(reader, path);

        return new PredicateDocIdSet(reader.MaxDoc, acceptDocs, doc =>
        {
            var elements = readElements(doc);

            if (elements.Count == 0)
            {
                return isAll;
            }

            return isAll ? elements.All(predicate) : elements.Any(predicate);
        });
    }
}
