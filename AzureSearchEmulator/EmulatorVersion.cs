using System.Reflection;

namespace AzureSearchEmulator;

/// <summary>
/// The release version of the running emulator, as set in the repository's VERSION file.
/// </summary>
public static class EmulatorVersion
{
    public static string Current { get; } = GetVersion();

    private static string GetVersion()
    {
        var assembly = typeof(EmulatorVersion).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            // The informational version carries a "+<commit sha>" suffix when the build has source
            // link enabled; callers want the version, not the provenance.
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
