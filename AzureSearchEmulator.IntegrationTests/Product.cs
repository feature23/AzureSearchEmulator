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
