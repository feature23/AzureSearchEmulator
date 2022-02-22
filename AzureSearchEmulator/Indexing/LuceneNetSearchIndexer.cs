using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Indexing;

public class LuceneNetSearchIndexer : ISearchIndexer
{
    private readonly ILuceneDirectoryFactory _luceneDirectoryFactory;

    public LuceneNetSearchIndexer(ILuceneDirectoryFactory luceneDirectoryFactory)
    {
        _luceneDirectoryFactory = luceneDirectoryFactory;
    }

    public Task<IndexDocumentsResult> IndexDocuments(SearchIndex index, IList<IndexDocumentAction> actions, CancellationToken cancellationToken = default)
    {
        var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, new StandardAnalyzer(LuceneVersion.LUCENE_48));

        using var directory = _luceneDirectoryFactory.GetDirectory(index.Name);
        using var writer = new IndexWriter(directory, config);

        var key = index.GetKeyField();

        var result = new IndexDocumentsResult();

        foreach (var action in actions)
        {
            if (action is MergeOrUploadIndexDocumentAction mergeOrUpload)
            {
                var keyNode = mergeOrUpload.Item[key.Name];

                if (keyNode == null)
                {
                    throw new InvalidOperationException($"Key value for key '{key.Name}' not found");
                }

                var keyTerm = new Term(key.Name, keyNode.GetValue<string>());

                var fields = from f in index.Fields
                    join v in mergeOrUpload.Item on f.Name equals v.Key
                    where v.Value != null
                    select CreateField(f, v.Value!);

                try
                {
                    writer.UpdateDocument(keyTerm, fields);
                    result.Value.Add(new IndexingResult(keyTerm.Text, true, 200));
                }
                catch (Exception ex)
                {
                    result.Value.Add(new IndexingResult(keyTerm.Text, $"{ex.GetType().Name}: {ex.Message}", false, 400));
                }
            }
            else
            {
                throw new NotImplementedException($"IndexDocumentAction type {action.GetType()} not yet implemented");
            }
        }

        writer.Commit();
        writer.Flush(true, true);

        return Task.FromResult(result);
    }

    private static IIndexableField CreateField(SearchField field, JsonNode value)
    {
        var stored = field.Retrievable ? Field.Store.YES : Field.Store.NO;

        return field.Type switch
        {
            "Edm.String" when field.Searchable.GetValueOrDefault(true) => new TextField(field.Name, value.GetValue<string>(), stored),
            "Edm.String" when !field.Searchable.GetValueOrDefault(true) => new StringField(field.Name, value.GetValue<string>(), stored),
            "Edm.Int32" => new Int32Field(field.Name, value.GetValue<int>(), stored),
            "Edm.Int64" => new Int64Field(field.Name, value.GetValue<long>(), stored),
            "Edm.Double" => new DoubleField(field.Name, value.GetValue<double>(), stored),
            "Edm.Boolean" => new Int32Field(field.Name, value.GetValue<bool>() ? 1 : 0, stored),
            "Edm.DateTimeOffset" => throw new NotImplementedException(),
            "Edm.GeographyPoint" => throw new NotImplementedException(),
            "Edm.ComplexType" => throw new NotImplementedException(),
            "Collection(Edm.String)" => throw new NotImplementedException(),
            "Collection(Edm.Int32)" => throw new NotImplementedException(),
            "Collection(Edm.Int64)" => throw new NotImplementedException(),
            "Collection(Edm.Double)" => throw new NotImplementedException(),
            "Collection(Edm.Boolean)" => throw new NotImplementedException(),
            "Collection(Edm.DateTimeOffset)" => throw new NotImplementedException(),
            "Collection(Edm.GeographyPoint)" => throw new NotImplementedException(),
            "Collection(Edm.ComplexType)" => throw new NotImplementedException(),
            _ => throw new InvalidOperationException($"Unsupported field type {field.Type}")
        };
    }
}