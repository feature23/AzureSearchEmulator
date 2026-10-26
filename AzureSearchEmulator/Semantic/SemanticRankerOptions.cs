using System.Security.Cryptography;
using System.Text;

namespace AzureSearchEmulator.Semantic;

/// <summary>
/// Settings for the opt-in model-backed semantic ranker, bound from <c>Emulator:SemanticRanker</c>
/// (issue #109).
/// </summary>
/// <remarks>
/// Semantic queries are answered either way. With the ranker disabled — the default — results are
/// reranked by a word-overlap heuristic that needs nothing downloaded, which is enough for an app
/// that only needs the response shape. Enabling it downloads a small open-weight cross-encoder at
/// startup and reranks with that instead, so the order is at least plausibly semantic.
/// </remarks>
public class SemanticRankerOptions
{
    public const string SectionName = "Emulator:SemanticRanker";

    /// <summary>
    /// <c>cross-encoder/ms-marco-MiniLM-L6-v2</c>, pinned to a commit so that every emulator
    /// downloads the same weights. Apache-2.0.
    /// </summary>
    private const string DefaultModelBaseUrl =
        "https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2/resolve/233902d25c440f23af6f7d6e94d2946bac0bee0a/";

    /// <summary>
    /// Rerank with the cross-encoder, downloading it at startup if it is not already cached.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Where downloaded models are cached. Relative paths resolve against the current directory,
    /// as <see cref="EmulatorOptions.IndexesDirectory"/> does.
    /// </summary>
    public string ModelDirectory { get; set; } = "models";

    /// <summary>
    /// The ONNX export of a BERT-style cross-encoder: one that takes <c>input_ids</c>,
    /// <c>attention_mask</c> and optionally <c>token_type_ids</c>, and returns a relevance logit.
    /// </summary>
    public string ModelUrl { get; set; } = DefaultModelBaseUrl + "onnx/model.onnx";

    /// <summary>
    /// The WordPiece vocabulary the model was trained with.
    /// </summary>
    public string VocabularyUrl { get; set; } = DefaultModelBaseUrl + "vocab.txt";

    /// <summary>
    /// The folder this model is cached in.
    /// </summary>
    /// <remarks>
    /// Named after the URLs rather than shared, so that pointing the settings at another model
    /// downloads it instead of quietly reusing the files already cached for the previous one.
    /// </remarks>
    public string GetCacheDirectory()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ModelUrl + "\n" + VocabularyUrl));

        return Path.Combine(Path.GetFullPath(ModelDirectory), Convert.ToHexStringLower(hash)[..16]);
    }

    public string GetModelPath() => Path.Combine(GetCacheDirectory(), "model.onnx");

    public string GetVocabularyPath() => Path.Combine(GetCacheDirectory(), "vocab.txt");
}
