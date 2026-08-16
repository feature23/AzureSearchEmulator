using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// A field to highlight, together with the Lucene field name it is indexed under.
/// </summary>
/// <remarks>
/// <paramref name="Path"/> differs from <c>Field.Name</c> only for a sub-field of a complex
/// type, which is indexed under its full slash-delimited path (i.e. <c>Address/City</c>).
/// </remarks>
public record HighlightField(SearchField Field, int MaxHighlights, string Path)
{
    public HighlightField(SearchField field, int maxHighlights)
        : this(field, maxHighlights, field.Name)
    {
    }
}
