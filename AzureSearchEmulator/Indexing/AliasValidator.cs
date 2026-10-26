using System.Text.RegularExpressions;
using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Indexing;

/// <summary>
/// Checks the parts of an alias definition that need no lookup. That the target exists and the
/// name is free of an index's is checked by the controller, which has the repositories.
/// </summary>
public static partial class AliasValidator
{
    /// <remarks>
    /// Azure's naming rules for an index, which an alias stands in for: 2 to 128 lower-case
    /// letters, digits, dashes and underscores, starting with a letter or digit, with no dash or
    /// underscore doubled.
    /// </remarks>
    [GeneratedRegex("^(?!.*(--|__))[a-z0-9][a-z0-9_-]{1,127}$")]
    private static partial Regex NamePattern();

    /// <remarks>
    /// The emulator does not hold index names to Azure's rules, so a target is only kept free of
    /// the characters that would let it escape the indexes directory.
    /// </remarks>
    [GeneratedRegex(@"^[^./\\]+$")]
    private static partial Regex TargetPattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>
    /// Whether a name from a URL could name an alias. Case is ignored, as the lookup ignores it.
    /// </summary>
    public static bool IsValidLookupName(string name) => IsValidName(name.ToLowerInvariant());

    /// <summary>
    /// Returns a message describing the first problem with the alias, or null when it is usable.
    /// </summary>
    public static string? FindInvalidAlias(SearchAlias searchAlias)
    {
        if (string.IsNullOrWhiteSpace(searchAlias.Name))
        {
            return "An alias must have a name.";
        }

        if (!IsValidName(searchAlias.Name))
        {
            return $"Alias name '{searchAlias.Name}' is invalid. Names are 2 to 128 characters long, contain only " +
                   "lower-case letters, digits, dashes and underscores, start with a letter or digit, and " +
                   "have no consecutive dashes or underscores.";
        }

        if (searchAlias.Indexes is not { Count: 1 } || string.IsNullOrWhiteSpace(searchAlias.Indexes[0]))
        {
            return $"Alias '{searchAlias.Name}' must target exactly one index.";
        }

        if (!TargetPattern().IsMatch(searchAlias.Indexes[0]))
        {
            return $"Alias '{searchAlias.Name}' targets '{searchAlias.Indexes[0]}', which is not a valid index name.";
        }

        return null;
    }
}
