using Lucene.Net.Search;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// A <see cref="DocIdSet"/> that evaluates a per-document predicate lazily as it is iterated,
/// used by filters whose exact test is too expensive to precompute — the geospatial geometry
/// tests, and the per-element evaluation of a complex collection's lambda.
/// </summary>
public class PredicateDocIdSet(int maxDoc, IBits? acceptDocs, Func<int, bool> match) : DocIdSet
{
    public override DocIdSetIterator GetIterator() => new Iterator(maxDoc, acceptDocs, match);

    // The predicate reads doc values, so it is not cheap enough to expose as random access;
    // returning null keeps Lucene iterating instead of calling Get() per document.
    public override IBits? Bits => null;

    public override bool IsCacheable => false;

    private sealed class Iterator(int maxDoc, IBits? acceptDocs, Func<int, bool> match) : DocIdSetIterator
    {
        private int _doc = -1;

        public override int DocID => _doc;

        public override int NextDoc() => Advance(_doc + 1);

        public override int Advance(int target)
        {
            // Advance may legitimately be called with NO_MORE_DOCS, and int.MaxValue + 1
            // would wrap around to int.MinValue and restart the scan from the beginning,
            // re-emitting every match. Exhaustion has to be sticky.
            if (_doc == NO_MORE_DOCS || target >= maxDoc)
            {
                return _doc = NO_MORE_DOCS;
            }

            for (var doc = target; doc < maxDoc; doc++)
            {
                if (acceptDocs?.Get(doc) == false)
                {
                    continue;
                }

                if (match(doc))
                {
                    return _doc = doc;
                }
            }

            return _doc = NO_MORE_DOCS;
        }

        public override long GetCost() => maxDoc;
    }
}
