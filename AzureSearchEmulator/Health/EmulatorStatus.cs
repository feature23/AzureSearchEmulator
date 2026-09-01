using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureSearchEmulator.Health;

/// <summary>
/// The health report plus the handful of facts about the running emulator the dashboard shows
/// alongside it (issue #90).
/// </summary>
public record EmulatorStatus(
    HealthStatus Status,
    TimeSpan TotalDuration,
    IReadOnlyList<EmulatorStatusEntry> Entries,
    string Version,
    string Environment,
    string IndexesDirectory,
    TimeSpan Uptime,
    DateTimeOffset CheckedAt);

public record EmulatorStatusEntry(
    string Name,
    HealthStatus Status,
    string? Description,
    TimeSpan Duration,
    IReadOnlyDictionary<string, object> Data);

/// <summary>
/// Produces an <see cref="EmulatorStatus"/> for the dashboard.
/// </summary>
/// <remarks>
/// The dashboard runs in-process, so it calls <see cref="HealthCheckService"/> directly instead of
/// fetching its own <c>/health</c> endpoint over HTTP. Going back out through the network stack
/// would mean the page needed to know its own externally reachable URL — which, behind the
/// container's port mapping or an Aspire proxy, it does not — to report on checks that are sitting
/// in the same process.
/// </remarks>
public class EmulatorStatusService(
    HealthCheckService healthCheckService,
    IHostEnvironment hostEnvironment,
    Microsoft.Extensions.Options.IOptions<EmulatorOptions> options,
    TimeProvider timeProvider)
{
    private static readonly string AssemblyVersion = GetAssemblyVersion();

    private static readonly long StartedAtTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    public async Task<EmulatorStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var report = await healthCheckService.CheckHealthAsync(cancellationToken);

        var entries = report.Entries
            .Select(i => new EmulatorStatusEntry(
                i.Key,
                i.Value.Status,
                // A failing check's exception message is the useful part and the description is
                // often null, so fall back to it rather than showing a bare "Unhealthy".
                i.Value.Description ?? i.Value.Exception?.Message,
                i.Value.Duration,
                i.Value.Data))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new EmulatorStatus(
            report.Status,
            report.TotalDuration,
            entries,
            AssemblyVersion,
            hostEnvironment.EnvironmentName,
            options.Value.IndexesDirectory,
            System.Diagnostics.Stopwatch.GetElapsedTime(StartedAtTimestamp),
            timeProvider.GetUtcNow());
    }

    private static string GetAssemblyVersion()
    {
        var assembly = typeof(EmulatorStatusService).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            // The informational version carries a "+<commit sha>" suffix when the build has source
            // link enabled; the dashboard wants the version, not the provenance.
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
