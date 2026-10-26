using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Lucene.Net.Index;
using Lucene.Net.Search;

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
            IndexDocument(context, keyTerm);
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

    protected static IEnumerable<IIndexableField> GetDocFields(SearchIndex index, JsonObject item)
    {
        return from f in index.Fields
            join v in item on f.Name equals v.Key
            where v.Value != null
            from indexField in f.CreateFields(v.Value!, index) // [!]: null checked by where clause
            select indexField;
    }

    /// <summary>
    /// Merges the batch item into the stored document and indexes the result as an upload would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole document is indexed again because <see cref="IndexSearcher.Doc(int)"/> returns
    /// stored fields only. Writing those back as they are would drop every indexed and doc-values
    /// copy of the fields the item does not supply, so filters, searches and vector queries would
    /// stop matching them.
    /// </para>
    /// <para>
    /// The item's fields replace the stored ones whole, as Azure does: a complex field or a
    /// collection is not merged element by element. An explicit null in the item clears the field,
    /// because <see cref="GetDocFields"/> skips null values.
    /// </para>
    /// <para>
    /// A field that is not retrievable has no stored copy (except a vector), so a merge that does
    /// not supply it loses its value.
    /// </para>
    /// </remarks>
    protected void MergeDocument(IndexingContext context, Term keyTerm, bool uploadIfMissing)
    {
        var searcher = new IndexSearcher(context.Reader.Value);

        var docs = searcher.Search(new TermQuery(keyTerm), 1);

        if (docs.TotalHits == 0 && !uploadIfMissing)
        {
            throw new DocumentNotFoundException();
        }

        var merged = new JsonObject();

        if (docs.TotalHits > 0)
        {
            var stored = searcher.Doc(docs.ScoreDocs[0].Doc);

            foreach (var field in context.Index.Fields)
            {
                if (field.GetStoredValue(stored) is { } value)
                {
                    merged[field.Name] = value;
                }
            }
        }

        foreach (var (name, value) in Item)
        {
            merged[name] = value?.DeepClone();
        }

        context.Writer.UpdateDocument(keyTerm, GetDocFields(context.Index, merged));
    }

    protected abstract void IndexDocument(IndexingContext context, Term keyTerm);
}
