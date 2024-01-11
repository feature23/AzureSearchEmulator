using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace AzureSearchEmulator.Indexing;

public abstract class UpsertIndexDocumentActionBase(JsonObject item) : IndexDocumentAction(item)
{
    public override IndexingResult PerformIndexingAsync(IndexingContext context)
    {
        var keyTerm = GetKeyTerm(context.Key);
        var fields = GetDocFields(context.Index);

        try
        {
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
            select f.CreateField(v.Value);
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

        foreach (var docField in docFields)
        {
            var field = doc.GetField(docField.Name);

            if (field != null)
            {
                doc.RemoveField(docField.Name);
            }

            doc.Add(docField);
        }

        context.Writer.UpdateDocument(keyTerm, doc.Fields);
    }

    protected abstract void IndexDocument(IndexingContext context, Term keyTerm, IEnumerable<IIndexableField> docFields);
}
