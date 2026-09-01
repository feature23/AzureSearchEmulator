using AzureSearchEmulator.Repositories;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureSearchEmulator.Health;

/// <summary>
/// Reports whether every index definition on disk still deserializes (issue #90).
/// </summary>
/// <remarks>
/// The definitions are plain JSON files in a directory users are told about and mount as a volume,
/// so hand-editing one is an expected thing to do. A malformed file does not surface until some
/// request happens to enumerate it, and then it surfaces as a 500 from an unrelated route —
/// <see cref="ISearchIndexRepository.GetAll"/> throws partway through, taking the listing with it.
/// Reporting it here names the file instead.
/// <para>
/// Degraded rather than Unhealthy: the emulator is still serving every index that does parse, and
/// the checks are aggregated into one overall status, so failing outright would claim the service
/// is down when one stray file is the whole of the problem.
/// </para>
/// </remarks>
public class IndexDefinitionsHealthCheck(ISearchIndexRepository searchIndexRepository) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var count = 0;

        try
        {
            await foreach (var index in searchIndexRepository.GetAll().WithCancellation(cancellationToken))
            {
                _ = index;
                count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Text.Json.JsonException)
        {
            return HealthCheckResult.Degraded(
                $"One or more index definitions could not be read: {ex.Message}",
                ex,
                new Dictionary<string, object> { ["indexesRead"] = count });
        }

        return HealthCheckResult.Healthy(
            count == 1 ? "1 index definition loaded." : $"{count} index definitions loaded.",
            new Dictionary<string, object> { ["indexCount"] = count });
    }
}
