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

    protected static void MergeDocument(IndexingContext context, Term keyTerm, IEnumerable<IIndexableField> docFields, bool uploadIfMissing)
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
        foreach (var docField in materialized)
        {
            doc.Add(docField);
        }

        RestoreVectorDocValues(context.Index, doc);

        context.Writer.UpdateDocument(keyTerm, doc.Fields);
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
