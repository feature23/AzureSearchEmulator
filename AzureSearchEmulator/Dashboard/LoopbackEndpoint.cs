namespace AzureSearchEmulator.Dashboard;

/// <summary>
/// Picks the address the dashboard's own SDK clients use to reach the emulator they run inside
/// (issue #92).
/// </summary>
/// <remarks>
/// The addresses come from Kestrel's bindings rather than from the browser's view of the page,
/// because the URL a user typed is not necessarily one the process can reach: behind the
/// container's port mapping or an Aspire proxy it names a port on the host, not inside the
/// container. Every binding Kestrel reports is one it is listening on, so connecting back over
/// loopback always works.
/// </remarks>
public static class LoopbackEndpoint
{
    /// <summary>
    /// Returns a connectable base URI for one of <paramref name="addresses"/>, or null when there is
    /// none.
    /// </summary>
    /// <remarks>
    /// Plain HTTP is preferred when it is bound: it saves a TLS handshake on a call that never
    /// leaves the process's own network stack. Wildcard hosts are listening addresses, not
    /// destinations, so they are swapped for loopback.
    /// </remarks>
    public static Uri? Resolve(IEnumerable<string> addresses)
    {
        var candidates = addresses
            .Select(Parse)
            .OfType<Uri>()
            .OrderBy(i => i.Scheme == Uri.UriSchemeHttp ? 0 : 1)
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static Uri? Parse(string address)
    {
        // Kestrel reports wildcard bindings as "+", "*", "0.0.0.0" or "[::]", and the first two are
        // not valid URI hosts, so they are replaced before parsing rather than after.
        var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);

        if (schemeEnd < 0)
        {
            return null;
        }

        var hostStart = schemeEnd + 3;
        var portStart = address.LastIndexOf(':');
        var host = portStart > hostStart ? address[hostStart..portStart] : address[hostStart..].TrimEnd('/');
        var rest = portStart > hostStart ? address[portStart..] : string.Empty;

        var loopbackHost = host switch
        {
            "+" or "*" or "[::]" => "localhost",
            "0.0.0.0" => "127.0.0.1",
            _ => host
        };

        if (!Uri.TryCreate($"{address[..hostStart]}{loopbackHost}{rest}", UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }
}
