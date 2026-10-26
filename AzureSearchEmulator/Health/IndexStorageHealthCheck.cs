using AzureSearchEmulator.Repositories;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AzureSearchEmulator.Health;

/// <summary>
/// Reports whether the emulator can actually read and write the directory its indexes live in
/// (issue #90).
/// </summary>
/// <remarks>
/// This is the check worth having. Everything the emulator does is backed by that directory, and
/// the ways it goes wrong are all environmental rather than code faults: a Docker volume mounted
/// read-only, a <c>dotnet tool</c> launched from a directory the user cannot write to, a path
/// configured to somewhere that does not exist. Each produces failures at index-creation or
/// indexing time that read like emulator bugs, so surfacing it as health turns a confusing 500
/// into a stated cause.
/// <para>
/// Writability is probed rather than inferred from file attributes, because on the platforms the
/// emulator runs on the attributes do not answer the question — a POSIX mode bit, an ACL, a
/// read-only bind mount and a full disk all present differently, and only an actual write covers
/// them all.
/// </para>
/// </remarks>
public class IndexStorageHealthCheck(IOptions<EmulatorOptions> options) : IHealthCheck
{
    private readonly EmulatorOptions _options = options.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var directory = _options.IndexesDirectory;

        var data = new Dictionary<string, object>
        {
            ["indexesDirectory"] = directory
        };

        if (!Directory.Exists(directory))
        {
            // Not a failure: the directory is created on first use, so its absence on a fresh
            // install is the normal state rather than a fault. What matters is whether the
            // emulator would be able to create it, which is what the parent's writability says.
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));

            if (parent == null || !Directory.Exists(parent))
            {
                return HealthCheckResult.Unhealthy(
                    $"The indexes directory '{directory}' does not exist and neither does its parent, so it cannot be created.",
                    data: data);
            }

            var parentProbe = await TryWrite(parent, cancellationToken);

            return parentProbe == null
                ? HealthCheckResult.Healthy("No indexes have been created yet.", data)
                : HealthCheckResult.Unhealthy(
                    $"The indexes directory '{directory}' does not exist and cannot be created: {parentProbe}",
                    data: data);
        }

        var probe = await TryWrite(directory, cancellationToken);

        if (probe != null)
        {
            return HealthCheckResult.Unhealthy(
                $"The indexes directory '{directory}' is not writable: {probe}",
                data: data);
        }

        return HealthCheckResult.Healthy("The indexes directory is readable and writable.", data);
    }

    /// <returns>null when the write succeeded, otherwise a description of why it did not.</returns>
    private static async Task<string?> TryWrite(string directory, CancellationToken cancellationToken)
    {
        // A uniquely named file, so two emulators sharing an indexes volume — or one being probed
        // concurrently — cannot collide and report each other's probe as a failure.
        var path = Path.Join(directory, $".healthcheck-{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(path, string.Empty, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The probe already answered the question it was asked; a leftover empty file is
                // not worth failing health over, and it is excluded from the *.index.json glob
                // the repository enumerates.
            }
        }
    }
}
