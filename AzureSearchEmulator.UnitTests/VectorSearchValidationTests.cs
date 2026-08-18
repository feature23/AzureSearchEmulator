using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for definition-time validation of vector search configuration (issue #46).
/// </summary>
/// <remarks>
/// The assertions check that a message names the thing at fault rather than matching it
/// verbatim, so that the wording can be improved without rewriting the tests.
/// </remarks>
public class VectorSearchValidationTests
{
    /// <summary>
    /// A valid starting point that each test breaks in one specific way.
    /// </summary>
    private static SearchIndex CreateIndex(
        Action<SearchIndex>? configure = null,
        Action<SearchField>? configureField = null)
    {
        var vectorField = new SearchField
        {
            Name = "embedding",
            Type = "Collection(Edm.Single)",
            Searchable = true,
            Filterable = false,
            Retrievable = false,
            Dimensions = 3,
            VectorSearchProfile = "vp"
        };

        configureField?.Invoke(vectorField);

        var index = new SearchIndex
        {
            Name = "vectors",
            Fields =
            [
                new SearchField { Name = "id", Type = "Edm.String", Key = true },
                vectorField
            ],
            VectorSearch = new VectorSearch
            {
                Algorithms = [new VectorSearchAlgorithm { Name = "algo", Kind = VectorSearchAlgorithmKind.Hnsw }],
                Profiles = [new VectorSearchProfile { Name = "vp", Algorithm = "algo" }]
            }
        };

        configure?.Invoke(index);

        return index;
    }

    [Fact]
    public void ValidConfiguration_IsAccepted()
    {
        Assert.Null(VectorSearchValidator.FindInvalidVectorSearch(CreateIndex()));
    }

    /// <summary>
    /// An index with no vector fields and no vector configuration must pass untouched, which is
    /// every index that existed before this feature.
    /// </summary>
    [Fact]
    public void IndexWithoutVectors_IsAccepted()
    {
        var index = new SearchIndex
        {
            Name = "hotels",
            Fields = [new SearchField { Name = "id", Type = "Edm.String", Key = true }]
        };

        Assert.Null(VectorSearchValidator.FindInvalidVectorSearch(index));
    }

    [Fact]
    public void VectorField_WithoutDimensions_IsRejected()
    {
        var index = CreateIndex(configureField: f => f.Dimensions = null);

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("dimensions", error);
        Assert.Contains("embedding", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4097)]
    public void VectorField_WithDimensionsOutOfRange_IsRejected(int dimensions)
    {
        var index = CreateIndex(configureField: f => f.Dimensions = dimensions);

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("dimensions", error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1536)]
    [InlineData(4096)]
    public void VectorField_WithDimensionsInRange_IsAccepted(int dimensions)
    {
        var index = CreateIndex(configureField: f => f.Dimensions = dimensions);

        Assert.Null(VectorSearchValidator.FindInvalidVectorSearch(index));
    }

    [Fact]
    public void VectorField_WithoutProfile_IsRejected()
    {
        var index = CreateIndex(configureField: f => f.VectorSearchProfile = null);

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("vectorSearchProfile", error);
    }

    /// <summary>
    /// The profile is where the metric comes from, so a field naming one that does not exist
    /// has no defined ordering for a query against it.
    /// </summary>
    [Fact]
    public void VectorField_NamingUnknownProfile_IsRejected()
    {
        var index = CreateIndex(configureField: f => f.VectorSearchProfile = "missing");

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("missing", error);
    }

    [Fact]
    public void Profile_NamingUnknownAlgorithm_IsRejected()
    {
        var index = CreateIndex(i => i.VectorSearch!.Profiles[0].Algorithm = "missing");

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("missing", error);
    }

    [Fact]
    public void Profile_WithoutAlgorithm_IsRejected()
    {
        var index = CreateIndex(i => i.VectorSearch!.Profiles[0].Algorithm = "");

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("algorithm", error);
    }

    [Fact]
    public void DuplicateProfileNames_AreRejected()
    {
        var index = CreateIndex(i => i.VectorSearch!.Profiles.Add(
            new VectorSearchProfile { Name = "VP", Algorithm = "algo" }));

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("more than one", error);
    }

    [Fact]
    public void DuplicateAlgorithmNames_AreRejected()
    {
        var index = CreateIndex(i => i.VectorSearch!.Algorithms.Add(
            new VectorSearchAlgorithm { Name = "ALGO" }));

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("more than one", error);
    }

    [Fact]
    public void UnnamedAlgorithm_IsRejected()
    {
        var index = CreateIndex(i => i.VectorSearch!.Algorithms[0].Name = "");

        Assert.Contains("name", VectorSearchValidator.FindInvalidVectorSearch(index));
    }

    /// <summary>
    /// A vectorizer needs a hosted embedding model, so an index declaring one would have to
    /// have its queries refused later; refusing the definition names the profile responsible.
    /// </summary>
    [Fact]
    public void Profile_WithVectorizer_IsRejected()
    {
        var index = CreateIndex(i => i.VectorSearch!.Profiles[0].Vectorizer = "myVectorizer");

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("vectorizer", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VectorField_ThatIsSortable_IsRejected()
    {
        var index = CreateIndex(configureField: f => f.Sortable = true);

        Assert.Contains("sortable", VectorSearchValidator.FindInvalidVectorSearch(index));
    }

    [Fact]
    public void VectorField_ThatIsFacetable_IsRejected()
    {
        var index = CreateIndex(configureField: f => f.Facetable = true);

        Assert.Contains("facetable", VectorSearchValidator.FindInvalidVectorSearch(index));
    }

    [Fact]
    public void VectorField_ThatIsKey_IsRejected()
    {
        var index = CreateIndex(configureField: f => f.Key = true);

        Assert.Contains("key", VectorSearchValidator.FindInvalidVectorSearch(index));
    }

    /// <summary>
    /// <c>filterable</c> defaults to true on this model, unlike Azure, so refusing it would
    /// refuse every vector field that did not explicitly turn it off — including definitions
    /// the service accepts.
    /// </summary>
    [Fact]
    public void VectorField_ThatIsFilterable_IsAccepted()
    {
        var index = CreateIndex(configureField: f => f.Filterable = true);

        Assert.Null(VectorSearchValidator.FindInvalidVectorSearch(index));
    }

    [Fact]
    public void NonVectorField_WithDimensions_IsRejected()
    {
        var index = CreateIndex(i => i.Fields[0].Dimensions = 128);

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("dimensions", error);
        Assert.Contains("id", error);
    }

    [Fact]
    public void NonVectorField_WithVectorSearchProfile_IsRejected()
    {
        var index = CreateIndex(i => i.Fields[0].VectorSearchProfile = "vp");

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("vectorSearchProfile", error);
    }

    /// <summary>
    /// A vector field is legal inside a complex type, and a mistake one level down deserves the
    /// same report as one at the top, naming the path that identifies it.
    /// </summary>
    [Fact]
    public void VectorSubField_IsValidated_AndNamedByPath()
    {
        var index = CreateIndex(i => i.Fields.Add(new SearchField
        {
            Name = "profile",
            Type = "Edm.ComplexType",
            Fields =
            [
                new SearchField
                {
                    Name = "embedding",
                    Type = "Collection(Edm.Single)",
                    Filterable = false,
                    VectorSearchProfile = "vp"
                    // Dimensions deliberately omitted.
                }
            ]
        }));

        var error = VectorSearchValidator.FindInvalidVectorSearch(index);

        Assert.Contains("dimensions", error);
        Assert.Contains("profile/embedding", error);
    }

    /// <summary>
    /// A vector's length is fixed once documents exist, so changing it has to be refused the way
    /// the other immutable field properties are.
    /// </summary>
    [Fact]
    public void ChangingDimensions_IsRejected()
    {
        var existing = CreateIndex();
        var updated = CreateIndex(configureField: f => f.Dimensions = 4);

        var error = IndexSchemaChangeValidator.FindDisallowedChange(existing, updated);

        Assert.NotNull(error);
        Assert.Contains("embedding", error);
    }

    /// <summary>
    /// Rebinding a field to a different profile changes only which metric a query uses, which
    /// does not invalidate anything already stored.
    /// </summary>
    [Fact]
    public void ChangingVectorSearchProfile_IsAllowed()
    {
        var existing = CreateIndex();
        var updated = CreateIndex(
            i => i.VectorSearch!.Profiles.Add(new VectorSearchProfile { Name = "other", Algorithm = "algo" }),
            f => f.VectorSearchProfile = "other");

        Assert.Null(IndexSchemaChangeValidator.FindDisallowedChange(existing, updated));
    }
}
