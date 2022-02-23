using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Index;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Indexing;

public class LuceneNetSearchIndexer : ISearchIndexer
{
    private readonly ILuceneDirectoryFactory _luceneDirectoryFactory;
    private readonly ILuceneIndexReaderFactory _luceneIndexReaderFactory;

    public LuceneNetSearchIndexer(ILuceneDirectoryFactory luceneDirectoryFactory, ILuceneIndexReaderFactory luceneIndexReaderFactory)
    {
        _luceneDirectoryFactory = luceneDirectoryFactory;
        _luceneIndexReaderFactory = luceneIndexReaderFactory;
    }

    public IndexDocumentsResult IndexDocuments(SearchIndex index, IList<IndexDocumentAction> actions)
    {
        var analyzer = AnalyzerHelper.GetPerFieldIndexAnalyzer(index.Fields);

        var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer);

        var directory = _luceneDirectoryFactory.GetDirectory(index.Name);
        using var writer = new IndexWriter(directory, config);

        var key = index.GetKeyField();

        var results = new IndexDocumentsResult();

        foreach (var action in actions)
        {
            var result = action.PerformIndexingAsync(index, key, writer);
            results.Value.Add(result);
        }

        writer.Commit();
        writer.Flush(true, true);

        _luceneIndexReaderFactory.RefreshReader(index.Name);

        return results;
    }
}