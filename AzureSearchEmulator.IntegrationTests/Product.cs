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

/// <summary>
/// A product carrying one field of every type a scoring function can read, used to exercise
/// scoring profiles (issue #47) end-to-end through the Azure Search SDK.
/// </summary>
/// <remarks>
/// Going through the SDK matters especially here: <c>ScoringProfile</c> and the four function
/// types are strongly typed in the SDK, so a definition the emulator stored in a shape the SDK
/// cannot read back would fail these tests rather than pass unnoticed.
/// </remarks>
public class ScoredProduct
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public double Rating { get; init; }

    public DateTimeOffset? Updated { get; init; }

    public GeoPoint? Location { get; init; }

    public string[] Tags { get; init; } = [];
}

/// <summary>
/// A hotel with a complex address and a collection of complex room objects, used to
/// exercise Edm.ComplexType and Collection(Edm.ComplexType) support (issue #7) end-to-end
/// through the Azure Search SDK.
/// </summary>
/// <remarks>
/// Going through the SDK matters here: it serializes these nested objects into the shape
/// Azure Search expects and deserializes the response back into them, so a document whose
/// sub-fields were flattened without being reassembled correctly would fail to round-trip
/// rather than pass unnoticed.
/// </remarks>
public class Hotel
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Nullable, so a hotel with no address covers the null-complex-value case.</summary>
    public Address? Address { get; init; }

    public Room[] Rooms { get; init; } = [];
}

/// <summary>A complex sub-object, itself containing a nested complex sub-object.</summary>
public class Address
{
    public string? Street { get; init; }

    public string? City { get; init; }

    public string? PostalCode { get; init; }

    public GeoCoordinates? Geo { get; init; }
}

/// <summary>Nested one level deeper, covering multi-level complex paths like Address/Geo/Lat.</summary>
public class GeoCoordinates
{
    public double Lat { get; init; }

    public double Lon { get; init; }
}

/// <summary>An element of a Collection(Edm.ComplexType), with a primitive collection of its own.</summary>
public class Room
{
    public string? Type { get; init; }

    public double BaseRate { get; init; }

    public bool SmokingAllowed { get; init; }

    public string[] Tags { get; init; } = [];
}
