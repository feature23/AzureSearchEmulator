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
