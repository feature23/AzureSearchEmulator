using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Searching;

public record HighlightField(SearchField Field, int MaxHighlights);
