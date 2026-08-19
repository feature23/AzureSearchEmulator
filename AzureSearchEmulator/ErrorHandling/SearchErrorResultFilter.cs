using System.Text.Json;
using AzureSearchEmulator.Controllers;
using AzureSearchEmulator.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.OData;

namespace AzureSearchEmulator.ErrorHandling;

/// <summary>
/// Rewrites the error results the controllers return into Azure Search's error envelope
/// (issue #40).
/// </summary>
/// <remarks>
/// <see cref="SearchErrorMiddleware"/> only sees exceptions. The controllers reject most bad
/// requests without throwing — <c>BadRequest(ModelState)</c>, <c>BadRequest(someMessage)</c>,
/// <c>NotFound($"...")</c> — and those bodies went out as a bare JSON string, an ASP.NET
/// <c>ValidationProblemDetails</c> document, or nothing at all. None is what the SDK reads: given
/// any of them <c>RequestFailedException.Message</c> degrades to "Service request failed.",
/// hiding a perfectly good explanation the emulator had already written.
///
/// Handling it here rather than at each of the ~30 call sites keeps the controllers reading
/// as they did and means a call site added later is covered without anyone remembering to
/// wrap it.
///
/// Only 4xx and 5xx results are rewritten. The 207 a partially-failing indexing batch returns
/// therefore passes through untouched, which it must: its per-item status objects are the
/// documented shape for that endpoint, and the failures it reports are per-document rather than
/// a fault in the request as a whole.
/// </remarks>
public class SearchErrorResultFilter : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not IStatusCodeActionResult { StatusCode: >= 400 and < 600 } result)
        {
            return;
        }

        var statusCode = result.StatusCode ?? StatusCodes.Status500InternalServerError;

        context.Result = new ContentResult
        {
            StatusCode = statusCode,
            ContentType = "application/json; charset=utf-8",
            Content = JsonSerializer.Serialize(new SearchErrorResponse
            {
                Error = new SearchError
                {
                    Code = CodeFor(statusCode),
                    Message = DescribeError(result, statusCode),
                },
            }),
        };
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private static string DescribeError(IStatusCodeActionResult result, int statusCode)
    {
        // The ODataController helpers do not produce an ObjectResult at all: BadRequest("...")
        // becomes a BadRequestODataResult holding an ODataError, which would otherwise have gone
        // out under the odata.error wrapper the SDK cannot read. Unwrapping it here is what keeps
        // messages like "Unknown scoring profile 'nope'" visible to the caller.
        if (ODataErrorMessage(result) is { } odataMessage)
        {
            return odataMessage;
        }

        var value = (result as ObjectResult)?.Value;

        return value switch
        {
            string text => text,
            ModelStateDictionary modelState => DescribeModelState(modelState),
            ValidationProblemDetails problem => DescribeModelErrors(problem.Errors),
            // What BadRequest(ModelState) actually yields once MVC has serialized it.
            SerializableError serializable => DescribeModelErrors(serializable
                .ToDictionary(entry => entry.Key, entry => entry.Value as string[] ?? [])),
            ProblemDetails problem when !string.IsNullOrEmpty(problem.Detail) => problem.Detail,
            ProblemDetails problem when !string.IsNullOrEmpty(problem.Title) => problem.Title,
            // Nothing to describe — BadRequest() and NotFound() with no argument — so fall
            // back to the status code's meaning rather than sending an empty message.
            _ => DefaultMessage(statusCode),
        };
    }

    /// <summary>
    /// Pulls the message out of the ODataError-carrying results the ODataController helpers
    /// return, or null when this result is not one of them.
    /// </summary>
    private static string? ODataErrorMessage(IStatusCodeActionResult result)
    {
        var error = result switch
        {
            BadRequestODataResult badRequest => badRequest.Error,
            NotFoundODataResult notFound => notFound.Error,
            ConflictODataResult conflict => conflict.Error,
            UnauthorizedODataResult unauthorized => unauthorized.Error,
            UnprocessableEntityODataResult unprocessable => unprocessable.Error,
            ODataErrorResult odataError => odataError.Error,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(error?.Message) ? null : error.Message;
    }

    private static string DescribeModelState(ModelStateDictionary modelState) =>
        DescribeModelErrors(modelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value!.Errors.Select(e => e.ErrorMessage).ToArray()));

    /// <summary>
    /// Flattens per-field validation errors into the single message the envelope carries.
    /// </summary>
    /// <remarks>
    /// Azure reports validation failures as one sentence, not a per-field map, and the field
    /// name is kept as a prefix so the caller can still tell which property was rejected.
    /// </remarks>
    private static string DescribeModelErrors(IDictionary<string, string[]> errors)
    {
        var described = errors
            .SelectMany(entry => entry.Value
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => string.IsNullOrEmpty(entry.Key) ? message : $"{entry.Key}: {message}"))
            .ToList();

        return described.Count > 0
            ? string.Join(" ", described)
            : DefaultMessage(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// The error code for a status, where Azure defines one.
    /// </summary>
    /// <remarks>
    /// Azure leaves <c>code</c> empty for query-time 400s and puts the whole diagnostic in the
    /// message, so only the name collision gets a code here. A 409 out of the index routes can
    /// only mean the name is taken — <see cref="IndexesController"/> answers the
    /// SearchIndexExistsException with a bare Conflict(), so the exception's own code never
    /// reaches the middleware.
    /// </remarks>
    private static string CodeFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status409Conflict => SearchErrorCodes.ResourceNameAlreadyInUse,
        _ => string.Empty,
    };

    private static string DefaultMessage(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "The request is invalid.",
        StatusCodes.Status404NotFound => "The requested resource was not found.",
        StatusCodes.Status409Conflict => "The resource already exists.",
        _ => "The request could not be completed.",
    };
}
