using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// The set of fields a <c>$select</c> asks for, as a tree mirroring the index's own shape.
/// </summary>
/// <remarks>
/// Azure Search's <c>$select</c> is a comma-delimited list of field paths, where a path may
/// name a complex field (<c>Address</c>, selecting the whole object) or reach inside one
/// (<c>Address/City</c>, selecting a single sub-field and nothing else beside it). Because a
/// selection can therefore be partial at any depth, it is held as a tree rather than a flat
/// set of names: each node says whether the field was selected outright, and if not, which of
/// its sub-fields were.
///
/// A null selection — no <c>$select</c> at all, or the <c>*</c> wildcard — means every
/// retrievable field, which is why <see cref="Parse"/> returns null for those rather than a
/// tree covering everything. Retrievability is applied separately and always wins: selecting
/// a non-retrievable field does not make it visible.
/// </remarks>
public sealed class FieldSelection
{
    /// <summary>
    /// Wildcard selecting every retrievable field, which Azure Search accepts in place of a
    /// field list.
    /// </summary>
    private const string Wildcard = "*";

    /// <summary>
    /// True when this field was named outright, and so is returned whole — including, for a
    /// complex field, all of its retrievable sub-fields.
    /// </summary>
    private bool _selectedWhole;

    /// <summary>
    /// Sub-fields selected individually, keyed by the schema's own casing. Empty once
    /// <see cref="_selectedWhole"/> is set, since the whole object subsumes any part of it.
    /// </summary>
    private readonly Dictionary<string, FieldSelection> _children =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a <c>$select</c> clause against the index, returning null when every
    /// retrievable field is wanted.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A path names a field that does not exist in the index, which Azure Search rejects
    /// rather than silently ignoring.
    /// </exception>
    public static FieldSelection? Parse(SearchIndex index, string? select)
    {
        if (string.IsNullOrWhiteSpace(select))
        {
            return null;
        }

        var paths = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (paths.Length == 0 || paths.Any(p => p == Wildcard))
        {
            return null;
        }

        var root = new FieldSelection();

        foreach (var path in paths)
        {
            // The canonical path is used from here on: Lucene field names and the sidecar
            // JSON both carry the schema's casing, not whatever the caller typed.
            if (!ComplexTypeSupport.TryResolvePath(index, path, out _, out var canonicalPath))
            {
                throw new InvalidOperationException(
                    $"Unable to find field '{path}' in the index '{index.Name}'");
            }

            root.Add(canonicalPath.Split(ComplexTypeSupport.PathSeparator));
        }

        return root;
    }

    private void Add(ReadOnlySpan<string> segments)
    {
        if (segments.IsEmpty)
        {
            // The path ended here, so this field is wanted whole. Any sub-fields picked out
            // by a narrower path — "Address/City" alongside "Address" — are redundant now.
            _selectedWhole = true;
            _children.Clear();
            return;
        }

        if (_selectedWhole)
        {
            return;
        }

        var segment = segments[0];

        if (!_children.TryGetValue(segment, out var child))
        {
            child = new FieldSelection();
            _children[segment] = child;
        }

        child.Add(segments[1..]);
    }

    /// <summary>
    /// True when <paramref name="fieldName"/> is selected, either outright or because some
    /// sub-field of it is.
    /// </summary>
    public bool Includes(string fieldName) => _selectedWhole || _children.ContainsKey(fieldName);

    /// <summary>
    /// The selection to apply within <paramref name="fieldName"/>, or null when the field was
    /// selected whole and so needs no further narrowing.
    /// </summary>
    public FieldSelection? GetSubSelection(string fieldName)
    {
        if (_selectedWhole)
        {
            return null;
        }

        var child = _children.GetValueOrDefault(fieldName);

        // A child selected whole narrows nothing within itself.
        return child is { _selectedWhole: true } ? null : child;
    }
}
