using Microsoft.Extensions.Options;

namespace AzureSearchEmulator.Semantic;

/// <summary>
/// Downloads the semantic ranker's model at startup when it is enabled and not already cached,
/// then loads it (issue #109).
/// </summary>
/// <remarks>
/// Runs as a hosted service so that it finishes before Kestrel starts listening: the emulator
/// never answers a semantic query with the heuristic because the model it was told to use is still
/// on its way. Any failure stops startup. The ranker is opt-in, so an emulator that cannot honour
/// the setting should say so rather than quietly fall back.
/// </remarks>
public sealed class SemanticModelDownloader(
    IOptions<SemanticRankerOptions> options,
    IServiceProvider services,
    ILogger<SemanticModelDownloader> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            return;
        }

        Directory.CreateDirectory(settings.GetCacheDirectory());

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        await Download(http, settings.VocabularyUrl, settings.GetVocabularyPath(), cancellationToken);
        await Download(http, settings.ModelUrl, settings.GetModelPath(), cancellationToken);

        // Loaded here rather than on the first semantic query, so that a corrupt or incompatible
        // model fails startup instead of that query.
        services.GetRequiredService<ISemanticScorer>();

        logger.LogInformation("Semantic ranker loaded from {Directory}", settings.GetCacheDirectory());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task Download(HttpClient http, string url, string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        logger.LogInformation("Downloading semantic ranker file {Url}", url);

        // Written beside the target and moved into place once complete, so an interrupted download
        // is fetched again on the next start rather than mistaken for a cached file.
        var partial = path + ".partial";

        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(partial);
            await source.CopyToAsync(target, cancellationToken);
        }

        File.Move(partial, path, overwrite: true);

        logger.LogInformation("Downloaded {Path} ({Bytes:N0} bytes)", path, new FileInfo(path).Length);
    }
}
