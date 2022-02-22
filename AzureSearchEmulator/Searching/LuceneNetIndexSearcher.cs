using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace AzureSearchEmulator.Searching;

public class LuceneNetIndexSearcher : IIndexSearcher
{
    private readonly ILuceneIndexReaderFactory _indexReaderFactory;

    public LuceneNetIndexSearcher(ILuceneIndexReaderFactory indexReaderFactory)
    {
        _indexReaderFactory = indexReaderFactory;
    }
    
    public Task<JsonObject?> GetDoc(SearchIndex index, string key)
    {
        var searcher = GetSearcher(index);

        var keyField = index.GetKeyField();

        var docs = searcher.Search(new TermQuery(new Term(keyField.Name, key)), 1);

        if (docs.TotalHits == 0)
        {
            return Task.FromResult<JsonObject?>(null);
        }

        var doc = searcher.Doc(docs.ScoreDocs[0].Doc);
        var result = new JsonObject();

        foreach (var field in index.Fields.Where(i => i.Retrievable))
        {
            var docField = doc.GetField(field.Name);

            if (docField != null)
            {
                result[field.Name] = field.Type switch
                {
                    "Edm.String" => docField.GetStringValue(),
                    "Edm.Int32" => docField.GetInt32Value(),
                    "Edm.Int64" => docField.GetInt64Value(),
                    "Edm.Double" => docField.GetDoubleValue(),
                    "Edm.Boolean" => docField.GetInt32Value() != 0,
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

        return Task.FromResult<JsonObject?>(result);
    }

    public Task<int> GetDocCount(SearchIndex index)
    {
        var reader = _indexReaderFactory.GetIndexReader(index.Name);

        return Task.FromResult(reader.NumDocs);
    }

    private IndexSearcher GetSearcher(SearchIndex index)
    {
        var reader = _indexReaderFactory.GetIndexReader(index.Name);

        return new IndexSearcher(reader);
    }
}