using Directory = Lucene.Net.Store.Directory;

namespace AzureSearchEmulator.SearchData;

/// <summary>
/// Measures what an index occupies in its Lucene directory, for the index statistics endpoint
/// (issue #91).
/// </summary>
public static class LuceneStorage
{
    /// <summary>
    /// The total size in bytes of the files in <paramref name="directory"/>: segments, deletions
    /// and the commit point, the same files a copy of the index would need.
    /// </summary>
    /// <remarks>
    /// An index that has never had a document written has no directory yet, and occupies nothing.
    /// A file can also disappear between listing and measuring it when a merge or commit removes
    /// an old segment, so it is skipped rather than failing the whole measurement.
    /// </remarks>
    public static long GetSize(Directory directory)
    {
        string[] files;

        try
        {
            files = directory.ListAll();
        }
        catch (IOException)
        {
            return 0;
        }

        long size = 0;

        foreach (var file in files)
        {
            try
            {
                size += directory.FileLength(file);
            }
            catch (IOException)
            {
                // Removed since it was listed.
            }
        }

        return size;
    }
}
