using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AzureSearchEmulator.Dashboard;

public enum JsonTokenKind
{
    Plain,
    Punctuation,
    Key,
    String,
    Number,
    Boolean,
    Null
}

public readonly record struct JsonToken(JsonTokenKind Kind, string Text);

/// <summary>
/// Indents JSON and splits it into classified runs for the search page's request and response views
/// (issue #92).
/// </summary>
/// <remarks>
/// Done on the server, in a few dozen lines, rather than by shipping a syntax-highlighting library
/// to the browser: the dashboard deliberately carries no client-side assets beyond Blazor's own
/// script, and JSON is a small enough grammar that classifying it is simpler than integrating
/// something that would.
/// </remarks>
public static class JsonHighlighter
{
    /// <summary>
    /// Bodies longer than this are shown indented but uncoloured. A page of results with vector
    /// fields selected runs to tens of thousands of numbers, and a span apiece is a render tree too
    /// large to be worth building and sending over the circuit for a view nobody reads number by
    /// number.
    /// </summary>
    public const int MaxHighlightedLength = 256 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        // Only ever rendered as text by Blazor, which does its own HTML encoding. The default
        // encoder would turn the <em> tags in highlights into <em> and make the raw
        // response harder to read than the one on the wire.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Re-indents <paramref name="json"/>, or returns it unchanged when it does not parse — an error
    /// body from somewhere other than the emulator is still worth showing as it arrived.
    /// </summary>
    public static string Format(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            using var buffer = new MemoryStream();

            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                document.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>
    /// Splits already-formatted JSON into tokens. Concatenating the tokens' text always reproduces
    /// the input exactly, whether or not it is valid JSON.
    /// </summary>
    public static IReadOnlyList<JsonToken> Tokenize(string json)
    {
        var tokens = new List<JsonToken>();

        if (json.Length > MaxHighlightedLength)
        {
            tokens.Add(new JsonToken(JsonTokenKind.Plain, json));
            return tokens;
        }

        var i = 0;

        while (i < json.Length)
        {
            var c = json[i];
            var start = i;

            if (c == '"')
            {
                i = SkipString(json, i);
                var kind = NextNonWhitespace(json, i) == ':' ? JsonTokenKind.Key : JsonTokenKind.String;
                Add(tokens, kind, json[start..i]);
            }
            else if (c is '-' || char.IsAsciiDigit(c))
            {
                while (i < json.Length && (char.IsAsciiDigit(json[i]) || json[i] is '-' or '+' or '.' or 'e' or 'E'))
                {
                    i++;
                }

                Add(tokens, JsonTokenKind.Number, json[start..i]);
            }
            else if (c is '{' or '}' or '[' or ']' or ',' or ':')
            {
                i++;
                Add(tokens, JsonTokenKind.Punctuation, json[start..i]);
            }
            else if (TryLiteral(json, i, "true") || TryLiteral(json, i, "false"))
            {
                i += c == 't' ? 4 : 5;
                Add(tokens, JsonTokenKind.Boolean, json[start..i]);
            }
            else if (TryLiteral(json, i, "null"))
            {
                i += 4;
                Add(tokens, JsonTokenKind.Null, json[start..i]);
            }
            else
            {
                i++;
                Add(tokens, JsonTokenKind.Plain, json[start..i]);
            }
        }

        return tokens;
    }

    /// <summary>
    /// Returns the index just past the string starting at <paramref name="start"/>, or the end of the
    /// input for a string that is never closed.
    /// </summary>
    private static int SkipString(string json, int start)
    {
        var i = start + 1;

        while (i < json.Length)
        {
            switch (json[i])
            {
                case '\\':
                    i += 2;
                    break;
                case '"':
                    return i + 1;
                default:
                    i++;
                    break;
            }
        }

        return json.Length;
    }

    private static char? NextNonWhitespace(string json, int from)
    {
        for (var i = from; i < json.Length; i++)
        {
            if (!char.IsWhiteSpace(json[i]))
            {
                return json[i];
            }
        }

        return null;
    }

    private static bool TryLiteral(string json, int at, string literal) =>
        string.CompareOrdinal(json, at, literal, 0, literal.Length) == 0;

    /// <summary>
    /// Appends a token, folding it into the previous one when both are plain text so whitespace and
    /// unrecognised characters do not each become a span of their own.
    /// </summary>
    private static void Add(List<JsonToken> tokens, JsonTokenKind kind, string text)
    {
        if (kind == JsonTokenKind.Plain && tokens.Count > 0 && tokens[^1].Kind == JsonTokenKind.Plain)
        {
            tokens[^1] = tokens[^1] with { Text = tokens[^1].Text + text };
            return;
        }

        tokens.Add(new JsonToken(kind, text));
    }
}
