using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Concurrency tests for document indexing.
///
/// IndexDocuments originally created a fresh IndexWriter per request and disposed it
/// at the end. Lucene permits only one IndexWriter per directory, so two concurrent
/// indexing requests against the same index raced for the directory's write.lock: the
/// loser failed with LockObtainFailedException, and under sustained concurrency a torn
/// commit corrupted the segment files permanently (FileNotFoundException: .../_N.si on
/// every subsequent write until the index directory was deleted from disk).
///
/// These tests exercise that path end-to-end through the SDK.
/// </summary>
public class ConcurrentIndexingTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    private const int Writers = 20;
    private const int BatchesPerWriter = 15;

    [Fact]
    public async Task ConcurrentBatches_SameIndex_AllSucceedAndAllDocumentsPersist()
    {
        const string indexName = "test-concurrent-same-index";
        var indexClient = factory.CreateSearchIndexClient();
        await CreateConcurrencyIndexAsync(indexClient, indexName);

        var searchClient = factory.CreateSearchClient(indexName);

        // Every writer owns a disjoint key range, so the final document count is
        // exact and independent of interleaving. Any missing document is a dropped
        // write, not a merge conflict.
        var writers = Enumerable.Range(0, Writers).Select(async writerId =>
        {
            var failures = new List<string>();

            for (var batchNo = 0; batchNo < BatchesPerWriter; batchNo++)
            {
                var docs = new[]
                {
                    new ConcurrencyDoc
                    {
                        Id = $"w{writerId}-b{batchNo}",
                        WriterId = writerId,
                        Payload = $"writer {writerId} batch {batchNo}"
                    }
                };

                try
                {
                    var response = await searchClient.IndexDocumentsAsync(
                        IndexDocumentsBatch.Upload(docs), cancellationToken: TestContext.Current.CancellationToken);

                    // Per-item failures do NOT throw by default — the SDK surfaces
                    // them on the result, so an unchecked call would silently pass.
                    foreach (var r in response.Value.Results.Where(r => !r.Succeeded))
                    {
                        failures.Add($"w{writerId}-b{batchNo} key={r.Key} status={r.Status}: {r.ErrorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"w{writerId}-b{batchNo} threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            return failures;
        }).ToList();

        var allFailures = (await Task.WhenAll(writers)).SelectMany(f => f).ToList();

        Assert.True(allFailures.Count == 0,
            $"{allFailures.Count}/{Writers * BatchesPerWriter} indexing requests failed under concurrency. " +
            $"First 10:{Environment.NewLine}{string.Join(Environment.NewLine, allFailures.Take(10))}");

        // The index must be intact and hold every document that was accepted.
        var count = await searchClient.GetDocumentCountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Writers * BatchesPerWriter, count.Value);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConcurrentBatches_IndexRemainsUsableAfterConcurrencyStops()
    {
        // The corruption this guards against is persistent: once segments are torn,
        // every subsequent write fails even single-threaded. A test that only asserts
        // on the concurrent phase can pass while leaving the index permanently broken,
        // so this asserts the index still accepts and serves writes afterward.
        const string indexName = "test-concurrent-index-health";
        var indexClient = factory.CreateSearchIndexClient();
        await CreateConcurrencyIndexAsync(indexClient, indexName);

        var searchClient = factory.CreateSearchClient(indexName);

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(async writerId =>
        {
            for (var batchNo = 0; batchNo < BatchesPerWriter; batchNo++)
            {
                try
                {
                    await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(new[]
                    {
                        new ConcurrencyDoc
                        {
                            Id = $"w{writerId}-b{batchNo}",
                            WriterId = writerId,
                            Payload = "load"
                        }
                    }));
                }
                catch
                {
                    // Failures during the load phase are asserted by the test above;
                    // here we only care whether the index survives them.
                }
            }
        }));

        // Single-threaded write to a quiesced index — must succeed on a healthy index.
        var postDoc = new ConcurrencyDoc { Id = "post-load", WriterId = -1, Payload = "written after load" };
        var postResponse = await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(new[] { postDoc }), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(postResponse.Value.Results.All(r => r.Succeeded),
            "Index is corrupt: a single-threaded write after the concurrent load failed — " +
            string.Join("; ", postResponse.Value.Results
                .Where(r => !r.Succeeded)
                .Select(r => $"{r.Key}: {r.ErrorMessage}")));

        var retrieved = await searchClient.GetDocumentAsync<ConcurrencyDoc>("post-load", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("written after load", retrieved.Value.Payload);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConcurrentBatches_DifferentIndexes_DoNotSerialize()
    {
        // Different indexes touch different Lucene directories and must remain
        // independent. This pins the requirement that a fix does not serialize
        // unrelated indexes behind a single global lock.
        const string indexA = "test-concurrent-multi-a";
        const string indexB = "test-concurrent-multi-b";
        var indexClient = factory.CreateSearchIndexClient();

        await CreateConcurrencyIndexAsync(indexClient, indexA);
        await CreateConcurrencyIndexAsync(indexClient, indexB);

        var clientA = factory.CreateSearchClient(indexA);
        var clientB = factory.CreateSearchClient(indexB);

        async Task Load(SearchClient client, string prefix)
        {
            for (var i = 0; i < BatchesPerWriter; i++)
            {
                await client.IndexDocumentsAsync(IndexDocumentsBatch.Upload(new[]
                {
                    new ConcurrencyDoc { Id = $"{prefix}-{i}", WriterId = 0, Payload = prefix }
                }));
            }
        }

        await Task.WhenAll(
            Task.WhenAll(Enumerable.Range(0, 5).Select(w => Load(clientA, $"a{w}"))),
            Task.WhenAll(Enumerable.Range(0, 5).Select(w => Load(clientB, $"b{w}"))));

        Assert.Equal(5 * BatchesPerWriter, (await clientA.GetDocumentCountAsync(TestContext.Current.CancellationToken)).Value);
        Assert.Equal(5 * BatchesPerWriter, (await clientB.GetDocumentCountAsync(TestContext.Current.CancellationToken)).Value);

        await indexClient.DeleteIndexAsync(indexA, TestContext.Current.CancellationToken);
        await indexClient.DeleteIndexAsync(indexB, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteIndex_AfterIndexing_ThenRecreate_Succeeds()
    {
        // Guards the writer-lifecycle hazard: if a cached IndexWriter is held open,
        // it keeps write.lock and segment file handles, while FileSearchIndexRepository
        // .Delete does Directory.Delete(folder, recursive: true) underneath it. On Linux
        // the unlink succeeds and the stale writer silently writes to orphaned inodes;
        // on Windows the delete throws. Either way, recreating the index with the same
        // name must yield a clean, writable index.
        const string indexName = "test-concurrent-delete-recreate";
        var indexClient = factory.CreateSearchIndexClient();

        await CreateConcurrencyIndexAsync(indexClient, indexName);
        var searchClient = factory.CreateSearchClient(indexName);

        await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(new[]
        {
            new ConcurrencyDoc { Id = "before-delete", WriterId = 0, Payload = "first generation" }
        }), cancellationToken: TestContext.Current.CancellationToken);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);

        // Recreate with the same name — must not inherit the previous writer or its documents.
        await CreateConcurrencyIndexAsync(indexClient, indexName);
        var recreatedClient = factory.CreateSearchClient(indexName);

        var response = await recreatedClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(new[]
        {
            new ConcurrencyDoc { Id = "after-recreate", WriterId = 0, Payload = "second generation" }
        }), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.Value.Results.All(r => r.Succeeded),
            "Write to the recreated index failed — a stale cached writer likely survived the delete: " +
            string.Join("; ", response.Value.Results.Where(r => !r.Succeeded).Select(r => r.ErrorMessage)));

        var count = await recreatedClient.GetDocumentCountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, count.Value);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    private static async Task CreateConcurrencyIndexAsync(SearchIndexClient indexClient, string indexName)
    {
        try
        {
            await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // expected
        }

        var index = new SearchIndex(indexName)
        {
            Fields =
            [
                new SearchField(nameof(ConcurrencyDoc.Id), SearchFieldDataType.String)
                    { IsKey = true, IsStored = true, IsFilterable = true },
                new SearchField(nameof(ConcurrencyDoc.WriterId), SearchFieldDataType.Int32)
                    { IsFilterable = true, IsStored = true },
                new SearchField(nameof(ConcurrencyDoc.Payload), SearchFieldDataType.String)
                    { IsSearchable = true, IsStored = true }
            ]
        };

        await indexClient.CreateIndexAsync(index);
    }
}

public class ConcurrencyDoc
{
    public string Id { get; set; } = string.Empty;
    public int WriterId { get; set; }
    public string Payload { get; set; } = string.Empty;
}
