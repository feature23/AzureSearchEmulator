using AzureSearchEmulator.Models;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

/// <summary>
/// One batch's access to the index: its writer, and a near-real-time reader the merge actions read the stored
/// document from.
/// </summary>
/// <remarks>
/// The reader is opened when first needed and does not see writes made after that. A read of a key this batch has
/// written since reopens it first, so a later action on the same key sees the earlier one's result. A batch that
/// writes each key once never reopens.
/// </remarks>
public sealed class IndexingContext(SearchIndex index, SearchField key, IndexWriter writer) : IDisposable
{
    private readonly HashSet<string> keysWrittenSinceReaderOpened = [];

    private DirectoryReader? reader;

    public SearchIndex Index { get; } = index;

    public SearchField Key { get; } = key;

    public IndexReader GetReader(Term keyTerm)
    {
        if (reader is null)
        {
            reader = writer.GetReader(true);
        }
        else if (keysWrittenSinceReaderOpened.Contains(keyTerm.Text))
        {
            var newer = DirectoryReader.OpenIfChanged(reader, writer, true);

            if (newer is not null)
            {
                reader.Dispose();
                reader = newer;
            }

            keysWrittenSinceReaderOpened.Clear();
        }

        return reader;
    }

    public void UpdateDocument(Term keyTerm, IEnumerable<IIndexableField> fields)
    {
        writer.UpdateDocument(keyTerm, fields);
        RecordWrite(keyTerm);
    }

    public void DeleteDocuments(Term keyTerm)
    {
        writer.DeleteDocuments(keyTerm);
        RecordWrite(keyTerm);
    }

    // A write made before the reader is first opened is visible to it anyway.
    private void RecordWrite(Term keyTerm)
    {
        if (reader is not null)
        {
            keysWrittenSinceReaderOpened.Add(keyTerm.Text);
        }
    }

    // An NRT reader pins segment files, so it must be released even when an action throws; otherwise the leak
    // blocks later deletion of those files on Windows.
    public void Dispose() => reader?.Dispose();
}
