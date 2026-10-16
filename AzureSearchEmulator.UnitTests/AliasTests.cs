using System.Text.Json;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using Microsoft.Extensions.Options;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Covers alias validation, file storage and resolution to an index.
/// </summary>
public sealed class AliasTests : IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "alias-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FileAliasRepository _aliases;
    private readonly FileSearchIndexRepository _indexes;
    private readonly IndexResolver _resolver;

    public AliasTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new EmulatorOptions { IndexesDirectory = _directory });
        _aliases = new FileAliasRepository(Options, options);
        _indexes = new FileSearchIndexRepository(Options, options);
        _resolver = new IndexResolver(_indexes, _aliases);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static SearchAlias Alias(string name, params string[] indexes) =>
        new() { Name = name, Indexes = [.. indexes] };

    [Fact]
    public void ValidAlias_IsAccepted() =>
        Assert.Null(AliasValidator.FindInvalidAlias(Alias("live", "products-v1")));

    [Theory]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("has.dot")]
    [InlineData("has/slash")]
    public void InvalidName_IsRejected(string name) =>
        Assert.NotNull(AliasValidator.FindInvalidAlias(Alias(name, "products-v1")));

    [Fact]
    public void NoTarget_IsRejected() =>
        Assert.Contains("exactly one", AliasValidator.FindInvalidAlias(Alias("live")));

    [Fact]
    public void TwoTargets_AreRejected() =>
        Assert.Contains("exactly one", AliasValidator.FindInvalidAlias(Alias("live", "a", "b")));

    [Fact]
    public void TargetWithPathCharacters_IsRejected() =>
        Assert.NotNull(AliasValidator.FindInvalidAlias(Alias("live", "../a")));

    [Fact]
    public async Task Repository_RoundTripsAnAliasAndItsUnmodelledProperties()
    {
        var alias = JsonSerializer.Deserialize<SearchAlias>(
            """{ "name": "Live", "indexes": ["a"], "extra": 1 }""", Options)!;

        await _aliases.Create(alias);

        var read = await _aliases.Get("live");

        Assert.Equal("Live", read!.Name);
        Assert.Equal(["a"], read.Indexes);
        Assert.Contains("extra", read.AdditionalProperties!.Keys);
    }

    [Fact]
    public async Task Repository_RefusesDuplicateCreate()
    {
        await _aliases.Create(Alias("live", "a"));

        await Assert.ThrowsAsync<SearchAliasExistsException>(() => _aliases.Create(Alias("LIVE", "b")));
    }

    [Fact]
    public async Task Repository_UpdateRepointsAndDeleteRemoves()
    {
        await _aliases.Create(Alias("live", "a"));
        await _aliases.Update(Alias("live", "b"));

        Assert.Equal(["b"], (await _aliases.Get("live"))!.Indexes);
        Assert.Single(await _aliases.GetAll().ToListAsync(TestContext.Current.CancellationToken));

        Assert.True(await _aliases.Delete(Alias("live", "b")));
        Assert.Null(await _aliases.Get("live"));
        Assert.False(await _aliases.Delete(Alias("live", "b")));
    }

    [Fact]
    public async Task Repository_ListingDoesNotIncludeIndexes()
    {
        await _indexes.Create(new SearchIndex { Name = "a" });
        await _aliases.Create(Alias("live", "a"));

        Assert.Equal(["live"], (await _aliases.GetAll().ToListAsync(TestContext.Current.CancellationToken)).Select(i => i.Name));
        Assert.Equal(["a"], (await _indexes.GetAll().ToListAsync(TestContext.Current.CancellationToken)).Select(i => i.Name));
    }

    [Fact]
    public async Task Resolver_ReturnsIndexByNameOrThroughAlias()
    {
        await _indexes.Create(new SearchIndex { Name = "a" });
        await _aliases.Create(Alias("live", "a"));

        Assert.Equal("a", (await _resolver.Get("a"))!.Name);
        Assert.Equal("a", (await _resolver.Get("live"))!.Name);
    }

    [Fact]
    public async Task Resolver_ReturnsNullForUnknownNameAndDanglingAlias()
    {
        await _aliases.Create(Alias("dangling", "gone"));

        Assert.Null(await _resolver.Get("nope"));
        Assert.Null(await _resolver.Get("dangling"));
    }
}
