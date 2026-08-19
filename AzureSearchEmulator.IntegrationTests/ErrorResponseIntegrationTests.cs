using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for Azure's error response shape (issue #40), run against a containerized
/// emulator.
/// </summary>
/// <remarks>
/// The tests come in two halves, because the issue has two halves. The raw-HTTP ones assert on
/// the body itself: the wrapper key must be <c>error</c> and not the <c>odata.error</c> that the
/// rest of the OData surface would suggest, since that is the difference between the SDK reading
/// the message and discarding it.
///
/// The SDK ones assert on what a consumer actually sees. That is the point of the issue —
/// <see cref="RequestFailedException.Status"/> drives retry policy, and a query fault answered
/// with a 500 would be retried against the emulator where the real service's 400 would not be.
/// </remarks>
public class ErrorResponseIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    private const string IndexName = "test-error-shape";

    private static SearchIndex BuildIndex(string indexName) =>
        new(indexName)
        {
            Fields =
            {
                new SearchField("id", SearchFieldDataType.String) { IsKey = true },
                new SearchField("title", SearchFieldDataType.String) { IsSearchable = true },
                new SimpleField("rating", SearchFieldDataType.Int32)
                {
                    IsFilterable = true,
                    IsSortable = true,
                },
            },
        };

    /// <summary>
    /// Creates the index and indexes a document.
    /// </summary>
    /// <remarks>
    /// The document matters: with an empty index the Lucene directory does not exist yet, so a
    /// query fails on the missing directory before it ever reaches the filter parser, and the
    /// test would assert on the wrong error.
    /// </remarks>
    private async Task<SearchIndexClient> SetUpAsync(string indexName)
    {
        var indexClient = factory.CreateSearchIndexClient();
        await indexClient.CreateIndexAsync(BuildIndex(indexName), TestContext.Current.CancellationToken);

        var searchClient = factory.CreateSearchClient(indexName);
        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload([
                new Dictionary<string, object> { ["id"] = "1", ["title"] = "widget", ["rating"] = 5 },
            ]),
            cancellationToken: TestContext.Current.CancellationToken);

        return indexClient;
    }

    /// <summary>
    /// The shape assertion: <c>{ "error": { "code": ..., "message": ... } }</c>.
    /// </summary>
    [Fact]
    public async Task MalformedFilter_ReturnsAzureErrorEnvelope()
    {
        const string indexName = $"{IndexName}-envelope";
        var indexClient = await SetUpAsync(indexName);
        var http = factory.CreateHttpClient();

        var response = await http.GetAsync(
            $"/indexes/{indexName}/docs?search=*&$filter=rating%20eq",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);

        Assert.False(document.RootElement.TryGetProperty("odata.error", out _),
            $"The envelope must use the 'error' key, not 'odata.error'. Body: {body}");

        var error = document.RootElement.GetProperty("error");

        // Present but empty is what Azure sends for a query-time 400, and the SDK omits its
        // "ErrorCode:" line rather than showing a blank one.
        Assert.Equal(string.Empty, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The status-code assertion, which is the half of the issue that changes client behavior.
    /// </summary>
    /// <remarks>
    /// Not every case here regressed: the search actions already caught InvalidOperationException
    /// per-action, so the first four returned a 400 before this change too. They are kept because
    /// that handling is now the filter's rather than the action's, and a 500 creeping back into
    /// any of them is exactly the regression worth catching. The last case is one that genuinely
    /// escaped as a 500.
    /// </remarks>
    [Theory]
    // Already answered with a 400 before this change, by the per-action catch blocks in
    // DocumentSearchingController — kept so that handling stays wired to the envelope.
    [InlineData("$filter=rating eq", "a filter the parser cannot read")]
    [InlineData("$filter=((rating eq 1", "an unbalanced filter expression")]
    [InlineData("$select=bogus", "an unknown field in $select")]
    [InlineData("$orderby=bogus", "an unknown field in $orderby")]
    // Was a 500 with an empty body: an operator the visitor has no translation for throws
    // NotImplementedException, which no catch block covered.
    [InlineData("$filter=rating div 2 eq 1", "an operator the emulator cannot translate")]
    public async Task MalformedQuery_IsRejectedAsBadRequest(string queryOption, string because)
    {
        var indexName = $"{IndexName}-{Math.Abs(queryOption.GetHashCode()):x}";
        var indexClient = await SetUpAsync(indexName);
        var http = factory.CreateHttpClient();

        var separator = queryOption.IndexOf('=');
        var name = queryOption[..separator];
        var value = Uri.EscapeDataString(queryOption[(separator + 1)..]);

        var response = await http.GetAsync(
            $"/indexes/{indexName}/docs?search=*&{name}={value}",
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400 for {because}, got {(int)response.StatusCode}. Body: {body}");

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Fetching a single document with an unknown field in <c>$select</c>.
    /// </summary>
    /// <remarks>
    /// Unlike the search actions, GetDocument had no catch block of its own, so the
    /// InvalidOperationException that FieldSelection.Parse raises escaped as a 500 with an empty
    /// body — the same mistake the search routes answered with a 400, reported two different ways
    /// depending on which route the caller took.
    /// </remarks>
    [Fact]
    public async Task DocumentLookupWithUnknownSelectField_IsRejectedAsBadRequest()
    {
        const string indexName = $"{IndexName}-doc-select";
        var indexClient = await SetUpAsync(indexName);
        var http = factory.CreateHttpClient();

        var response = await http.GetAsync(
            $"/indexes/{indexName}/docs/1?$select=bogus",
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 400, got {(int)response.StatusCode}. Body: {body}");

        using var document = JsonDocument.Parse(body);
        var message = document.RootElement.GetProperty("error").GetProperty("message").GetString();

        Assert.Contains("bogus", message!, StringComparison.Ordinal);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An unexpected fault still answers in the envelope, and without leaking internals.
    /// </summary>
    /// <remarks>
    /// Searching an index that has never had a document written to it currently trips a Lucene
    /// DirectoryNotFoundException, because the segment directory is only created on first write.
    /// That 500 is arguably its own bug — the real service answers an empty result set — so this
    /// test deliberately asserts only on the SHAPE of an unexpected failure, not on this query
    /// being one. Should the empty-index case be fixed to return results, the assertions below
    /// stop applying and the test should be pointed at another unexpected fault rather than
    /// being made to keep this 500 alive.
    ///
    /// The generic message is the point: the underlying exception names the emulator's own
    /// filesystem paths, and those belong in the log rather than the response body.
    /// </remarks>
    [Fact]
    public async Task UnexpectedFailure_ReturnsEnvelopeWithoutInternalDetail()
    {
        const string indexName = $"{IndexName}-unexpected";
        var indexClient = factory.CreateSearchIndexClient();
        await indexClient.CreateIndexAsync(BuildIndex(indexName), TestContext.Current.CancellationToken);

        var http = factory.CreateHttpClient();
        var response = await http.GetAsync(
            $"/indexes/{indexName}/docs?search=*",
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Guard the premise: if this stops being a server fault, the assertions below are no
        // longer testing what they claim to.
        Assert.True(response.StatusCode == HttpStatusCode.InternalServerError,
            $"This test needs a query that faults unexpectedly; got {(int)response.StatusCode}. Body: {body}");

        using var document = JsonDocument.Parse(body);
        var message = document.RootElement.GetProperty("error").GetProperty("message").GetString();

        Assert.Equal("An unexpected error occurred.", message);
        Assert.DoesNotContain("/app", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// What the consumer sees: the SDK reads the message out of the envelope instead of
    /// falling back to its own "Service request failed."
    /// </summary>
    [Fact]
    public async Task MalformedFilter_SurfacesMessageThroughSdk()
    {
        const string indexName = $"{IndexName}-sdk-filter";
        var indexClient = await SetUpAsync(indexName);
        var searchClient = factory.CreateSearchClient(indexName);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            searchClient.SearchAsync<SearchDocument>("*",
                new SearchOptions { Filter = "rating eq" },
                TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.BadRequest, ex.Status);
        Assert.DoesNotContain("Service request failed.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rating eq", ex.Message, StringComparison.Ordinal);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The one case with a real Azure error code, so it is the one place ErrorCode can be
    /// asserted rather than the empty string a query-time 400 carries.
    /// </summary>
    [Fact]
    public async Task DuplicateIndex_SurfacesResourceNameAlreadyInUse()
    {
        const string indexName = $"{IndexName}-duplicate";
        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateIndexAsync(BuildIndex(indexName), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            indexClient.CreateIndexAsync(BuildIndex(indexName), TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.Conflict, ex.Status);
        Assert.Equal("ResourceNameAlreadyInUse", ex.ErrorCode);
        Assert.Contains(indexName, ex.Message, StringComparison.Ordinal);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A 404 carries its explanation too — the message was previously dropped by the OData
    /// result helpers, leaving the SDK with only the status code.
    /// </summary>
    [Fact]
    public async Task MissingIndex_SurfacesMessageThroughSdk()
    {
        var searchClient = factory.CreateSearchClient("test-error-shape-absent");

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            searchClient.SearchAsync<SearchDocument>("*",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.NotFound, ex.Status);
        Assert.DoesNotContain("Service request failed.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("test-error-shape-absent", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Validation failures reported through ModelState reach the envelope as one message, with
    /// the field name kept as a prefix.
    /// </summary>
    [Fact]
    public async Task InvalidIndexDefinition_ReportsValidationMessage()
    {
        await factory.WaitUntilServingAsync();
        var http = factory.CreateHttpClient();

        // A field with no name at all — the SDK will not construct this, so it goes as raw JSON.
        var content = new StringContent(
            """{"name":"test-error-shape-invalid","fields":[{"type":"Edm.String"}]}""",
            Encoding.UTF8,
            "application/json");

        var response = await http.PostAsync("/indexes", content, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var message = document.RootElement.GetProperty("error").GetProperty("message").GetString();

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("Name", message, StringComparison.Ordinal);
    }
}
