using System.Text.RegularExpressions;
using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Indexing;

/// <summary>
/// Checks the parts of an alias definition that need no lookup. That the target exists and the
/// name is free of an index's is checked by the controller, which has the repositories.
/// </summary>
public static partial class AliasValidator
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>
    /// Returns a message describing the first problem with the alias, or null when it is usable.
    /// </summary>
    public static string? FindInvalidAlias(SearchAlias alias)
    {
        if (string.IsNullOrWhiteSpace(alias.Name))
        {
            return "An alias must have a name.";
        }

        if (!IsValidName(alias.Name))
        {
            return $"Alias name '{alias.Name}' is invalid. Names start with a letter or digit, " +
                   "contain only letters, digits, dashes and underscores, and are at most 128 characters.";
        }

        if (alias.Indexes is not { Count: 1 } || string.IsNullOrWhiteSpace(alias.Indexes[0]))
        {
            return $"Alias '{alias.Name}' must target exactly one index.";
        }

        if (!IsValidName(alias.Indexes[0]))
        {
            return $"Alias '{alias.Name}' targets '{alias.Indexes[0]}', which is not a valid index name.";
        }

        return null;
    }
}
