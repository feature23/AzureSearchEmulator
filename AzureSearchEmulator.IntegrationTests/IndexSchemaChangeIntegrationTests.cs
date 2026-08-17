using System.Net;
using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for refusing breaking field changes on <c>CreateOrUpdateIndexAsync</c>
/// (issue #32), run against a containerized emulator.
/// </summary>
/// <remarks>
/// These go through the Azure Search SDK rather than raw HTTP because the divergence the issue
/// reports is one a consumer hits through the SDK: the call that succeeds locally throws
/// <see cref="RequestFailedException"/> against the real service. Asserting on the exception the
/// SDK surfaces is what shows the emulator now fails in the same place, and in the same shape,
/// that production would.
/// </remarks>
public class IndexSchemaChangeIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    private static SearchIndex BuildIndex(string indexName, bool titleFilterable) =>
        new(indexName)
        {
            Fields =
            {
                new SearchField("Id", SearchFieldDataType.String) { IsKey = true },
                new SearchField("Title", SearchFieldDataType.String)
                {
                    IsSearchable = true,
                    IsFilterable = titleFilterable
                }
            }
        };

    /// <summary>
    /// The exact reproduction from the issue: toggling <c>IsFilterable</c> on a field that
    /// already exists.
    /// </summary>
    [Fact]
    public async Task CreateOrUpdate_TogglingIsFilterableOnExistingField_IsRefused()
    {
        const string indexName = "test-schema-change-filterable";
        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateIndexAsync(BuildIndex(indexName, titleFilterable: false),
            TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            indexClient.CreateOrUpdateIndexAsync(BuildIndex(indexName, titleFilterable: true),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains("Existing field 'Title' cannot be changed.", ex.Message);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateOrUpdate_ChangingFieldType_IsRefused()
    {
        const string indexName = "test-schema-change-type";
        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateIndexAsync(BuildIndex(indexName, titleFilterable: false),
            TestContext.Current.CancellationToken);

        var retyped = new SearchIndex(indexName)
        {
            Fields =
            {
                new SearchField("Id", SearchFieldDataType.String) { IsKey = true },
                new SearchField("Title", SearchFieldDataType.Int32) { IsFilterable = false }
            }
        };

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            indexClient.CreateOrUpdateIndexAsync(retyped,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains("Existing field 'Title' cannot be changed.", ex.Message);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateOrUpdate_RemovingExistingField_IsRefused()
    {
        const string indexName = "test-schema-change-removal";
        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateIndexAsync(BuildIndex(indexName, titleFilterable: false),
            TestContext.Current.CancellationToken);

        var withoutTitle = new SearchIndex(indexName)
        {
            Fields = { new SearchField("Id", SearchFieldDataType.String) { IsKey = true } }
        };

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            indexClient.CreateOrUpdateIndexAsync(withoutTitle,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains("Existing field 'Title' cannot be deleted.", ex.Message);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Re-sending an unchanged definition has to keep working: an indexer that calls
    /// <c>CreateOrUpdate</c> on every tick would otherwise be broken by this check rather than
    /// helped by it.
    /// </summary>
    [Fact]
    public async Task CreateOrUpdate_WithUnchangedDefinition_Succeeds()
    {
        const string indexName = "test-schema-change-noop";
        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateIndexAsync(BuildIndex(indexName, titleFilterable: false),
            TestContext.Current.CancellationToken);

        var response = await indexClient.CreateOrUpdateIndexAsync(
            BuildIndex(indexName, titleFilterable: false),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Title", response.Value.Fields[1].Name);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Adding a field is the schema change Azure Search does allow, and the one consumers rely
    /// on to evolve an index in place.
    /// </summary>
    [Fact]
    public async Task CreateOrUpdate_AddingNewField_Succeeds()
    {
        const string indexName = "test-schema-change-add";
        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateIndexAsync(BuildIndex(indexName, titleFilterable: false),
            TestContext.Current.CancellationToken);

        var extended = BuildIndex(indexName, titleFilterable: false);
        extended.Fields.Add(new SearchField("Rating", SearchFieldDataType.Double) { IsFilterable = true });

        var response = await indexClient.CreateOrUpdateIndexAsync(extended,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, response.Value.Fields.Count);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }
}
