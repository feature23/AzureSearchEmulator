using System.Text.Json;
using AzureSearchEmulator.ErrorHandling;
using AzureSearchEmulator.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.OData;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for the error envelope (issue #40) — the mapping from exception or action result
/// onto Azure's <c>{ "error": { ... } }</c> body, without a container in the way.
/// </summary>
public class ErrorResponseTests
{
    /// <summary>
    /// A malformed <c>$filter</c> is the case the issue leads with: the parser throws
    /// <see cref="ODataException"/>, which used to escape as a 500 where Azure answers 400.
    /// </summary>
    [Fact]
    public async Task ODataException_IsMappedToBadRequest()
    {
        var (statusCode, error) = await RunMiddlewareAsync(new ODataException("Expression expected."));

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Equal("Expression expected.", error.Message);
        Assert.Equal(string.Empty, error.Code);
    }

    /// <summary>
    /// The query layer's ~55 NotImplementedException sites: an emulator gap, but only reachable
    /// by sending a query, and a 500 would tell the client to retry what can never succeed.
    /// </summary>
    [Theory]
    [InlineData(typeof(NotImplementedException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task RequestFaults_AreMappedToBadRequest(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "the query is not supported")!;

        var (statusCode, error) = await RunMiddlewareAsync(exception);

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Equal("the query is not supported", error.Message);
    }

    [Fact]
    public async Task SearchIndexExists_IsMappedToConflictWithAzureCode()
    {
        var (statusCode, error) = await RunMiddlewareAsync(new SearchIndexExistsException("products"));

        Assert.Equal(StatusCodes.Status409Conflict, statusCode);
        Assert.Equal("ResourceNameAlreadyInUse", error.Code);
        Assert.Contains("products", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unexpected fault keeps its detail out of the response: the text can name internal
    /// paths, and the caller can do nothing with it either way.
    /// </summary>
    [Fact]
    public async Task UnexpectedException_IsMappedToGenericServerError()
    {
        var (statusCode, error) = await RunMiddlewareAsync(
            new IOException("/var/lib/emulator/indexes/products/segments.gen is locked"));

        Assert.Equal(StatusCodes.Status500InternalServerError, statusCode);
        Assert.Equal("An unexpected error occurred.", error.Message);
        Assert.DoesNotContain("/var/lib", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Once the response is on the wire the envelope cannot be written, so the exception has to
    /// keep propagating rather than being swallowed into a half-written body.
    /// </summary>
    [Fact]
    public async Task ExceptionAfterResponseStarted_IsRethrown()
    {
        var context = new DefaultHttpContext();

        // DefaultHttpContext's stock response feature always reports HasStarted as false, so the
        // started state has to be supplied explicitly for the guard to be exercised at all.
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());

        var middleware = new SearchErrorMiddleware(
            _ => throw new InvalidOperationException("too late"),
            NullLogger<SearchErrorMiddleware>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(context));

        Assert.Equal("too late", ex.Message);
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }

    /// <summary>
    /// The ODataController helpers hold their message in an ODataError rather than an
    /// ObjectResult, and would otherwise have emitted it under the odata.error key the SDK
    /// cannot read.
    /// </summary>
    [Fact]
    public void ODataResult_MessageIsCarriedIntoTheEnvelope()
    {
        var result = new BadRequestODataResult("Unknown scoring profile 'nope'.");

        var (statusCode, error) = RunFilter(result);

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Equal("Unknown scoring profile 'nope'.", error.Message);
    }

    /// <summary>
    /// BadRequest(ModelState) reaches the filter as a SerializableError; the field name is kept
    /// as a prefix so the caller can still tell what was rejected.
    /// </summary>
    [Fact]
    public void ModelStateErrors_AreFlattenedIntoOneMessage()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("Top", "Page size must be between 0 and 1000");

        var (statusCode, error) = RunFilter(new BadRequestObjectResult(new SerializableError(modelState)));

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Contains("Top", error.Message, StringComparison.Ordinal);
        Assert.Contains("Page size must be between 0 and 1000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainStringBody_BecomesTheMessage()
    {
        var (_, error) = RunFilter(new NotFoundObjectResult("The specified index does not exist."));

        Assert.Equal("The specified index does not exist.", error.Message);
    }

    /// <summary>
    /// A result with nothing to describe still gets a message, rather than an empty one.
    /// </summary>
    [Fact]
    public void EmptyResult_FallsBackToTheStatusMeaning()
    {
        var (statusCode, error) = RunFilter(new NotFoundResult());

        Assert.Equal(StatusCodes.Status404NotFound, statusCode);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    /// <summary>
    /// Success results must pass through untouched — the filter runs on every action, including
    /// the 207 batch responses whose per-item shape is the documented contract.
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(207)]
    [InlineData(204)]
    public void NonErrorResults_AreLeftAlone(int statusCode)
    {
        var original = new ObjectResult(new { value = 1 }) { StatusCode = statusCode };
        var context = ResultExecutingContext(original);

        new SearchErrorResultFilter().OnResultExecuting(context);

        Assert.Same(original, context.Result);
    }

    private static async Task<(int StatusCode, SearchError Error)> RunMiddlewareAsync(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var middleware = new SearchErrorMiddleware(
            _ => throw exception,
            NullLogger<SearchErrorMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        return (context.Response.StatusCode, Deserialize(body));
    }

    private static (int StatusCode, SearchError Error) RunFilter(IActionResult result)
    {
        var context = ResultExecutingContext(result);

        new SearchErrorResultFilter().OnResultExecuting(context);

        var rewritten = Assert.IsType<ContentResult>(context.Result);

        return (rewritten.StatusCode ?? 0, Deserialize(rewritten.Content ?? string.Empty));
    }

    private static ResultExecutingContext ResultExecutingContext(IActionResult result) =>
        new(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            [],
            result,
            controller: new object());

    /// <summary>
    /// Reads the body back through the envelope, which also asserts the wrapper key: a body
    /// using odata.error would leave Error null here and fail every caller.
    /// </summary>
    private static SearchError Deserialize(string body)
    {
        var response = JsonSerializer.Deserialize<SearchErrorResponse>(body);

        Assert.NotNull(response);
        Assert.NotNull(response.Error);
        Assert.DoesNotContain("odata.error", body, StringComparison.Ordinal);

        return response.Error;
    }
}
