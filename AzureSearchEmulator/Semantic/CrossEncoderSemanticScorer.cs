using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace AzureSearchEmulator.Semantic;

/// <summary>
/// Scores passages with an open-weight cross-encoder run through ONNX Runtime (issue #109).
/// </summary>
/// <remarks>
/// A cross-encoder reads the query and the passage together and returns one relevance logit,
/// which is the same job Azure's semantic ranker does, if at a far smaller scale. The default
/// model is MiniLM-L6 trained on MS MARCO: English only and nowhere near Azure's quality, but its
/// order is driven by meaning rather than shared words, which is what a caller testing semantic
/// ranking locally is after.
/// </remarks>
public sealed class CrossEncoderSemanticScorer : ISemanticScorer, IDisposable
{
    /// <summary>
    /// BERT's position limit. Longer passages are cut, as Azure's ranker also cuts its input.
    /// </summary>
    private const int MaxSequenceLength = 512;

    /// <summary>
    /// The query's share of <see cref="MaxSequenceLength"/>, so a long query cannot crowd the
    /// passage out.
    /// </summary>
    private const int MaxQueryTokens = 64;

    private const int BatchSize = 16;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _takesTokenTypes;
    private readonly string _outputName;

    public CrossEncoderSemanticScorer(string modelPath, string vocabularyPath)
    {
        _session = new InferenceSession(modelPath);
        _tokenizer = BertTokenizer.Create(vocabularyPath, new BertOptions { LowerCaseBeforeTokenization = true });

        // DistilBERT-style exports have no segment input, and feeding an input the model does not
        // declare is an error rather than a no-op.
        _takesTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        _outputName = _session.OutputMetadata.Keys.First();
    }

    public IReadOnlyList<double> Score(string query, IReadOnlyList<string> passages)
    {
        var scores = new double[passages.Count];

        var queryIds = Encode(query, MaxQueryTokens);

        for (var start = 0; start < passages.Count; start += BatchSize)
        {
            var count = Math.Min(BatchSize, passages.Count - start);
            var batch = new List<int>[count];

            for (var i = 0; i < count; i++)
            {
                var passageIds = Encode(passages[start + i], MaxSequenceLength - queryIds.Count - 3);

                // [CLS] query [SEP] passage [SEP]
                batch[i] = [_tokenizer.ClassificationTokenId, .. queryIds, _tokenizer.SeparatorTokenId,
                    .. passageIds, _tokenizer.SeparatorTokenId];
            }

            var logits = Run(batch, queryIds.Count + 2);

            for (var i = 0; i < count; i++)
            {
                scores[start + i] = 1.0 / (1.0 + Math.Exp(-logits[i]));
            }
        }

        return scores;
    }

    private List<int> Encode(string text, int maxTokens)
    {
        var ids = _tokenizer.EncodeToIds(text, addSpecialTokens: false, considerPreTokenization: true, considerNormalization: true);

        return ids.Count <= maxTokens ? ids.ToList() : ids.Take(Math.Max(0, maxTokens)).ToList();
    }

    /// <param name="firstSegmentLength">
    /// How many leading tokens of every sequence are the query and its delimiters, which BERT
    /// marks as segment 0 and the passage after them as segment 1.
    /// </param>
    private float[] Run(List<int>[] batch, int firstSegmentLength)
    {
        var width = batch.Max(s => s.Count);
        var inputIds = new long[batch.Length * width];
        var attentionMask = new long[batch.Length * width];
        var tokenTypes = new long[batch.Length * width];

        for (var row = 0; row < batch.Length; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var offset = row * width + col;
                var isToken = col < batch[row].Count;

                inputIds[offset] = isToken ? batch[row][col] : _tokenizer.PaddingTokenId;
                attentionMask[offset] = isToken ? 1 : 0;
                tokenTypes[offset] = isToken && col >= firstSegmentLength ? 1 : 0;
            }
        }

        long[] shape = [batch.Length, width];

        var names = new List<string> { "input_ids", "attention_mask" };
        var values = new List<OrtValue>
        {
            OrtValue.CreateTensorValueFromMemory(inputIds, shape),
            OrtValue.CreateTensorValueFromMemory(attentionMask, shape),
        };

        if (_takesTokenTypes)
        {
            names.Add("token_type_ids");
            values.Add(OrtValue.CreateTensorValueFromMemory(tokenTypes, shape));
        }

        try
        {
            using var runOptions = new RunOptions();
            using var outputs = _session.Run(runOptions, names, values, [_outputName]);

            var output = outputs[0].GetTensorDataAsSpan<float>();
            var columns = output.Length / batch.Length;
            var logits = new float[batch.Length];

            for (var row = 0; row < batch.Length; row++)
            {
                // One column is a relevance logit. Two are not-relevant/relevant logits, whose
                // difference is the same quantity.
                logits[row] = columns == 1
                    ? output[row]
                    : output[row * columns + columns - 1] - output[row * columns];
            }

            return logits;
        }
        finally
        {
            foreach (var value in values)
            {
                value.Dispose();
            }
        }
    }

    public void Dispose() => _session.Dispose();
}
