using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Indexing;

public abstract class UpsertIndexDocumentActionBase(JsonObject item) : IndexDocumentAction(item)
{
    /// <summary>
    /// The index fields this batch item set to an explicit null, populated before
    /// <see cref="IndexDocument"/> runs so a merge can clear them (issue #71).
    /// </summary>
    /// <remarks>
    /// Carried on the action rather than added to <see cref="IndexDocument"/>'s signature
    /// because only the merge actions have any use for it; upload replaces the whole document,
    /// where a null means "no value" and there is nothing to clear.
    /// </remarks>
    protected IReadOnlyCollection<SearchField> ClearedFields { get; private set; } = [];

    public override IndexingResult PerformIndexingAsync(IndexingContext context)
    {
        // GetKeyTerm throws when the batch item omits the key field. It has to run inside
        // the try: outside, the throw escapes IndexDocuments and 500s the whole batch,
        // taking every other item's result with it — including the summary log that is
        // supposed to be the record of which writes were dropped.
        Term keyTerm;

        try
        {
            keyTerm = GetKeyTerm(context.Key);
        }
        catch (Exception ex)
        {
            return new IndexingResult(string.Empty, $"{ex.GetType().Name}: {ex.Message}", false, 400);
        }

        try
        {
            var fields = GetDocFields(context.Index);
            ClearedFields = GetClearedFields(context.Index).ToList();
            IndexDocument(context, keyTerm, fields);
            return new IndexingResult(keyTerm.Text, true, 200);
        }
        catch (DocumentNotFoundException)
        {
            return new IndexingResult(keyTerm.Text, $"The document with key {keyTerm.Text} could not be found", false, 404);
        }
        catch (Exception ex)
        {
            return new IndexingResult(keyTerm.Text, $"{ex.GetType().Name}: {ex.Message}", false, 400);
        }
    }

    protected IEnumerable<IIndexableField> GetDocFields(SearchIndex index)
    {
        return from f in index.Fields
            join v in Item on f.Name equals v.Key
            where v.Value != null
            from indexField in f.CreateFields(v.Value!) // [!]: null checked by where clause
            select indexField;
    }

    /// <summary>
    /// The fields the batch item named with an explicit JSON null, which a merge clears
    /// (issue #71).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Azure documents setting a field to null on a merge as the way to remove its value, and
    /// distinguishes that from omitting the field, which leaves the existing value alone.
    /// <see cref="GetDocFields"/> cannot carry the distinction: a null produces no Lucene field,
    /// so a merge driven only by that list has nothing to tell it which names to remove and
    /// silently keeps the old value.
    /// </para>
    /// <para>
    /// Only the field names are needed, so the value itself is never converted — which is what
    /// makes this safe for types that would reject a null, such as a geography point or a
    /// vector whose length must match <c>dimensions</c>.
    /// </para>
    /// </remarks>
    protected IEnumerable<SearchField> GetClearedFields(SearchIndex index)
    {
        return from f in index.Fields
            join v in Item on f.Name equals v.Key
            where v.Value is null
            select f;
    }

    protected static void MergeDocument(IndexingContext context, Term keyTerm, IEnumerable<IIndexableField> docFields, bool uploadIfMissing)
        => MergeDocument(context, keyTerm, docFields, [], uploadIfMissing);

    protected static void MergeDocument(
        IndexingContext context,
        Term keyTerm,
        IEnumerable<IIndexableField> docFields,
        IEnumerable<SearchField> clearedFields,
        bool uploadIfMissing)
    {
        var reader = context.Reader.Value;
        var searcher = new IndexSearcher(reader);

        var docs = searcher.Search(new TermQuery(keyTerm), 1);

        if (docs.TotalHits == 0 && !uploadIfMissing)
        {
            throw new DocumentNotFoundException();
        }

        var doc = docs.TotalHits == 0 ? new Document() : searcher.Doc(docs.ScoreDocs[0].Doc);

        var materialized = docFields.ToList();
        foreach (var name in materialized.Select(f => f.Name).Distinct())
        {
            doc.RemoveFields(name);
        }

        // Runs after the replacements are removed and before they are added, so a field that is
        // both cleared and supplied — which the JSON object cannot express, but a caller could
        // reach through a complex field's sub-fields — keeps the supplied value.
        foreach (var name in clearedFields.SelectMany(GetLuceneFieldNames))
        {
            doc.RemoveFields(name);
        }

        foreach (var docField in materialized)
        {
            doc.Add(docField);
        }

        RestoreVectorDocValues(context.Index, doc);

        context.Writer.UpdateDocument(keyTerm, doc.Fields);
    }

    /// <summary>
    /// Every Lucene field name a single index field can write, so that clearing it removes the
    /// whole set rather than the retrievable copy alone (issue #71).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One index field routinely occupies several Lucene names: a searchable string that is also
    /// filterable writes a second, unanalyzed copy under <c>__azs_raw__</c>; a facetable field
    /// adds doc values; a geography point writes a latitude, a longitude and a doc-values entry;
    /// a vector writes a stored sidecar and a packed copy. Removing only the field's own name
    /// would leave those behind, and a document still carrying the raw copy of a cleared string
    /// keeps matching filters for a value it no longer has.
    /// </para>
    /// <para>
    /// The names are produced by asking each helper rather than by reconstructing the prefixes
    /// here, so a convention added later is picked up by changing its own helper's call site
    /// alone. Names are emitted unconditionally instead of being predicted from the field's
    /// attributes: <see cref="Document.RemoveFields"/> ignores a name the document does not
    /// carry, which makes over-listing free and under-listing the only real failure.
    /// </para>
    /// <para>
    /// A complex field holds no value of its own, so clearing one means clearing every leaf
    /// beneath it, plus the storage and doc-values entries the complex field itself writes.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> GetLuceneFieldNames(SearchField field)
    {
        if (field.IsComplex())
        {
            yield return ComplexTypeSupport.GetComplexStorageFieldName(field.Name);
            yield return ComplexTypeSupport.GetComplexElementsDocValuesFieldName(field.Name);
        }

        foreach (var (path, _) in ComplexTypeSupport.EnumerateLeafFields(field))
        {
            yield return path;
            yield return SearchFieldExtensions.GetRawStringFieldName(path);
            yield return SearchFieldExtensions.GetCollectionStorageFieldName(path);
            yield return FacetSupport.GetFacetDocValuesFieldName(path);
            yield return GeoSupport.GetLatFieldName(path);
            yield return GeoSupport.GetLonFieldName(path);
            yield return GeoSupport.GetPointDocValuesFieldName(path);
            yield return VectorSearchSupport.GetVectorDocValuesFieldName(path);
        }
    }

    /// <summary>
    /// Rebuilds the packed doc values of every vector field the merged document still holds a
    /// stored value for (issue #46).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IndexSearcher.Doc(int)"/> returns stored fields and nothing else, so the
    /// document reconstructed above has lost every doc-values field it had. For a vector that
    /// matters more than it looks: the stored JSON sidecar is what retrieval reads, so a merged
    /// document keeps coming back correctly, while the packed copy a query scans is gone — the
    /// document silently stops being findable by vector search while still looking intact.
    /// </para>
    /// <para>
    /// Rebuilding from the sidecar is possible precisely because the sidecar survives, which is
    /// why it is the authoritative copy. A vector whose sidecar is absent is skipped rather than
    /// zero-filled: the field simply has no value for this document, and inventing one would put
    /// it in results it does not belong in.
    /// </para>
    /// <para>
    /// Deliberately scoped to vector fields. The same reconstruction drops facet, geo and
    /// complex-element doc values too, which is a pre-existing fault worth fixing on its own
    /// terms rather than quietly here, where it would be neither tested nor reviewed as such.
    /// </para>
    /// </remarks>
    private static void RestoreVectorDocValues(SearchIndex index, Document doc)
    {
        foreach (var (path, field) in ComplexTypeSupport.EnumerateLeafFields(index))
        {
            if (!field.IsVectorField())
            {
                continue;
            }

            var docValuesName = VectorSearchSupport.GetVectorDocValuesFieldName(path);

            // Present already when this merge supplied the vector itself, in which case the
            // fields built for it are the ones to keep.
            if (doc.GetField(docValuesName) != null)
            {
                continue;
            }

            var storedJson = doc.Get(SearchFieldExtensions.GetCollectionStorageFieldName(path));

            if (storedJson == null || JsonNode.Parse(storedJson) is not JsonArray array)
            {
                continue;
            }

            var vector = VectorSearchSupport.ParseVector(path, array, field.Dimensions);

            doc.Add(new BinaryDocValuesField(docValuesName, new BytesRef(VectorSearchSupport.PackVector(vector))));
        }
    }

    protected abstract void IndexDocument(IndexingContext context, Term keyTerm, IEnumerable<IIndexableField> docFields);
}
