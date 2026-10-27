using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests for the version the emulator reports on startup and on the dashboard (issue #89).
/// </summary>
public class EmulatorVersionTests
{
    [Fact]
    public void Current_MatchesVersionFile()
    {
        // Read from the VERSION file rather than the assembly, so this fails if the build stops
        // stamping the assembly from it.
        var expected = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "VERSION")).Trim();

        Assert.Equal(expected, EmulatorVersion.Current);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AzureSearchEmulator.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory.FullName;
    }
}
