using AzureSearchEmulator.Models;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public class IndexingContext(SearchIndex index, SearchField key, IndexWriter writer, Lazy<IndexReader> reader)
{
    public SearchIndex Index { get; } = index;

    public SearchField Key { get; } = key;

    public IndexWriter Writer { get; } = writer;

    public Lazy<IndexReader> Reader { get; } = reader;
}
