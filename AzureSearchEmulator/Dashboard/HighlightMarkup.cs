using System.Net;
using Microsoft.AspNetCore.Components;

namespace AzureSearchEmulator.Dashboard;

/// <summary>
/// Renders hit-highlight fragments and semantic captions with their emphasis intact (issue #92).
/// </summary>
/// <remarks>
/// The fragments are document text with <c>&lt;em&gt;</c> tags spliced in, and document text is
/// whatever was indexed — so it cannot be handed to the page as markup. Everything is encoded, and
/// only the exact tags the emulator inserts are turned back into elements.
/// </remarks>
public static class HighlightMarkup
{
    public static MarkupString Render(string fragment)
    {
        var encoded = WebUtility.HtmlEncode(fragment)
            .Replace("&lt;em&gt;", "<mark>", StringComparison.Ordinal)
            .Replace("&lt;/em&gt;", "</mark>", StringComparison.Ordinal);

        return new MarkupString(encoded);
    }
}
