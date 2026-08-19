using System.Text.Json;
using AzureSearchEmulator.Models;
using Microsoft.OData;

namespace AzureSearchEmulator.ErrorHandling;

/// <summary>
/// Turns exceptions escaping the pipeline into Azure Search's error envelope, with the status
/// code Azure would use (issue #40).
/// </summary>
/// <remarks>
/// Before this, nothing handled exceptions outside Development, so every fault became an
/// unstructured 500. That matters most for malformed queries: a bad <c>$filter</c> throws
/// <see cref="ODataException"/> out of the parser and an unknown field throws
/// <see cref="InvalidOperationException"/> out of the searcher, both of which Azure answers
/// with a 400. Client code that branches on the status — retry policies especially, since a
/// 500 is retryable and a 400 is not — behaved differently against the emulator than against
/// the service, which is exactly what an emulator must not do.
///
/// The mapping treats the query layer's <see cref="NotImplementedException"/> sites as 400s
/// rather than 500s. Strictly they are emulator gaps, not caller mistakes, but they are
/// reached only by writing a query the emulator cannot translate, and Azure — which
/// implements the whole language — answers the same query text with a 400. Reporting 500
/// would tell a client to retry a request that can never succeed.
/// </remarks>
public class SearchErrorMiddleware(RequestDelegate next, ILogger<SearchErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            // The response may already be on the wire — a failure partway through streaming
            // results, say. Nothing can be rewritten at that point; the truncated body and
            // the dropped connection are the only signals available, so let it propagate.
            if (context.Response.HasStarted)
            {
                logger.LogError(ex, "Exception after the response had started; cannot write an error body.");
                throw;
            }

            var (statusCode, code, message) = Map(ex);

            if (statusCode >= StatusCodes.Status500InternalServerError)
            {
                logger.LogError(ex, "Unhandled exception; returning {StatusCode}.", statusCode);
            }
            else
            {
                // Request faults are the caller's to fix and are ordinary traffic for an
                // emulator, so they stay at Debug rather than filling the log with stack
                // traces for every mistyped filter.
                logger.LogDebug(ex, "Request rejected with {StatusCode}.", statusCode);
            }

            context.Response.Clear();
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";

            var payload = new SearchErrorResponse
            {
                Error = new SearchError { Code = code, Message = message },
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
        }
    }

    /// <remarks>
    /// The code is left empty for the query-time failures: Azure reports those with an empty
    /// <c>code</c> and the whole diagnostic in <c>message</c>, and the SDK omits its
    /// <c>ErrorCode:</c> line entirely when the code is blank.
    /// </remarks>
    private static (int StatusCode, string Code, string Message) Map(Exception ex) => ex switch
    {
        // A filter or order-by the parser rejects outright.
        ODataException => (StatusCodes.Status400BadRequest, string.Empty, ex.Message),

        // A query the emulator cannot translate — see the remarks above on why this is a 400.
        NotImplementedException or NotSupportedException =>
            (StatusCodes.Status400BadRequest, string.Empty, ex.Message),

        // Validation failures raised by the searcher and the indexing layer: an unknown field
        // in $select or $orderby, a missing suggester, a scoring profile the index lacks.
        InvalidOperationException => (StatusCodes.Status400BadRequest, string.Empty, ex.Message),

        SearchIndexExistsException =>
            (StatusCodes.Status409Conflict, SearchErrorCodes.ResourceNameAlreadyInUse, ex.Message),

        // The message is deliberately generic: an unexpected fault's text can carry file
        // paths and other internals, and the log above keeps the detail for whoever runs
        // the emulator.
        _ => (StatusCodes.Status500InternalServerError, string.Empty, "An unexpected error occurred."),
    };
}
