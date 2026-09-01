using System.Text.Json;
using AzureSearchEmulator.Health;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests for the health checks behind the dashboard and <c>/health</c> (issue #90).
/// </summary>
/// <remarks>
/// These run against real directories rather than an abstraction over the filesystem, because the
/// conditions the checks exist to catch — a directory that is missing, or present but not writable
/// — are filesystem states, and a mocked <c>IFileSystem</c> reporting them would only be testing
/// the mock's own opinion of what a failed write looks like.
/// </remarks>
public class HealthCheckTests : IDisposable
{
    private readonly string _root =
        Path.Join(Path.GetTempPath(), $"azsearchemu-health-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // A directory the writability test chmod'ed has to be made writable again or the delete
        // fails and leaks the temp folder. Nothing is chmod'ed on Windows, where that test skips.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.Delete(_root, recursive: true);
    }

    private IndexStorageHealthCheck CreateStorageCheck(string directory) =>
        new(Options.Create(new EmulatorOptions { IndexesDirectory = directory }));

    private static Task<HealthCheckResult> Run(IHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task IndexStorage_WithWritableDirectory_IsHealthy()
    {
        Directory.CreateDirectory(_root);

        var result = await Run(CreateStorageCheck(_root));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(_root, result.Data["indexesDirectory"]);
    }

    /// <remarks>
    /// The emulator creates the indexes directory on first use, so a fresh install has none. That
    /// is the normal state rather than a fault, and reporting it as unhealthy would mean every
    /// first run of the tool showed a red dashboard.
    /// </remarks>
    [Fact]
    public async Task IndexStorage_WhenDirectoryDoesNotYetExist_IsHealthy()
    {
        Directory.CreateDirectory(_root);
        var notYetCreated = Path.Join(_root, "indexes");

        var result = await Run(CreateStorageCheck(notYetCreated));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task IndexStorage_WhenNeitherDirectoryNorParentExists_IsUnhealthy()
    {
        var unreachable = Path.Join(_root, "no", "such", "path");

        var result = await Run(CreateStorageCheck(unreachable));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("cannot be created", result.Description);
    }

    /// <remarks>
    /// The case the check is really for: a Docker volume mounted read-only, or a tool launched from
    /// a directory the user cannot write to. Without it the failure first appears as a 500 from
    /// whichever indexing request happened to run.
    /// </remarks>
    [Fact]
    public async Task IndexStorage_WhenDirectoryIsNotWritable_IsUnhealthy()
    {
        // Written as an if rather than Assert.SkipWhen so the platform analyzer can see that
        // SetUnixFileMode is only reached off Windows; Skip throws, which it cannot narrow on.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are the mechanism being used to make the directory read-only.");
            return;
        }

        Assert.SkipWhen(Environment.UserName == "root",
            "root writes to a read-only directory regardless of its mode.");

        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var result = await Run(CreateStorageCheck(_root));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not writable", result.Description);
    }

    [Fact]
    public async Task IndexStorage_LeavesNoProbeFileBehind()
    {
        Directory.CreateDirectory(_root);

        await Run(CreateStorageCheck(_root));

        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task IndexDefinitions_WithReadableDefinitions_ReportsTheCount()
    {
        Directory.CreateDirectory(_root);
        WriteIndex("products");
        WriteIndex("people");

        var result = await Run(new IndexDefinitionsHealthCheck(CreateRepository()));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(2, result.Data["indexCount"]);
    }

    /// <remarks>
    /// Degraded rather than Unhealthy: the emulator still serves every index that parses, and the
    /// checks aggregate into one overall status, so failing outright would claim the whole service
    /// is down over one hand-edited file.
    /// </remarks>
    [Fact]
    public async Task IndexDefinitions_WithAMalformedDefinition_IsDegraded()
    {
        Directory.CreateDirectory(_root);
        WriteIndex("products");
        File.WriteAllText(Path.Join(_root, "broken.index.json"), "{ this is not json");

        var result = await Run(new IndexDefinitionsHealthCheck(CreateRepository()));

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task IndexDefinitions_WithNoIndexes_IsHealthy()
    {
        Directory.CreateDirectory(_root);

        var result = await Run(new IndexDefinitionsHealthCheck(CreateRepository()));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(0, result.Data["indexCount"]);
    }

    private ISearchIndexRepository CreateRepository() =>
        new FileSearchIndexRepository(
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            Options.Create(new EmulatorOptions { IndexesDirectory = _root }));

    private void WriteIndex(string name)
    {
        var index = new SearchIndex
        {
            Name = name,
            Fields = [new SearchField { Name = "Id", Type = "Edm.String", Key = true }]
        };

        File.WriteAllText(
            Path.Join(_root, $"{name}.index.json"),
            JsonSerializer.Serialize(index, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
