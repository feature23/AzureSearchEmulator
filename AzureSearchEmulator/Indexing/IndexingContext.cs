using AzureSearchEmulator.Models;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public class IndexingContext
{
    public IndexingContext(SearchIndex index, SearchField key, IndexWriter writer, Lazy<IndexReader> reader)
    {
        Index = index;
        Key = key;
        Writer = writer;
        Reader = reader;
    }

    public SearchIndex Index { get; }
    
    public SearchField Key { get; }
    
    public IndexWriter Writer { get; }
    
    public Lazy<IndexReader> Reader { get; }
}