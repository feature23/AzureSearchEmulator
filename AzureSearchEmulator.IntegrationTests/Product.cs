using Azure.Core.GeoJson;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Product model for use with Azure Search SDK serialization.
/// </summary>
public class Product
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public double Price { get; init; }

    public required string Category { get; init; }

    public bool InStock { get; init; }
}

/// <summary>
/// A real-world city, used to exercise Edm.GeographyPoint support (issue #5) end-to-end
/// through the Azure Search SDK.
/// </summary>
/// <remarks>
/// The SDK serializes <see cref="GeoPoint"/> to and from the GeoJSON representation Azure
/// Search uses on the wire, so these tests cover the real serialization path rather than a
/// hand-built JSON body.
/// </remarks>
public class City
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Country { get; init; }

    /// <summary>
    /// Nullable so that a city with no location can be indexed, covering how Azure Search
    /// treats a null geography point in filters and in $orderby.
    /// </summary>
    public GeoPoint? Location { get; init; }

    public int Population { get; init; }
}

/// <summary>
/// Product model with collection fields, used to exercise Collection(Edm.*) support
/// (issue #6) end-to-end through the Azure Search SDK.
/// </summary>
public class TaggedProduct
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string[] Tags { get; init; }

    public required int[] Sizes { get; init; }
}
