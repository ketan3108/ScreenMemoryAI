using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using ScreenMemory.AI.Core.Services;

namespace ScreenMemory.AI.App.Services;

public sealed class AiSemanticService : IAiSemanticService, IDisposable
{
    private const int MaxTokens = 128;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private string _availabilityState = "HeuristicOnly";

    private static readonly Dictionary<string, CategoryProfile> CategoryProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = new("Code Snippet", 0.95f,
        [
            "function", "class ", "public ", "private ", "async ", "await ", "const ", "var ",
            "let ", "=>", "namespace", "interface ", "return ", "void ", "string ", "bool ",
            "using ", "import ", "from "
        ]),
        ["error"] = new("Error Log", 0.93f,
        [
            "exception", "error:", "failed", "stack trace", "timeout", "unhandled", "404",
            "500", "cannot ", "invalid ", "undefined", "nullreference"
        ]),
        ["financial"] = new("Financial Data", 0.9f,
        [
            "$", "invoice", "receipt", "payment", "balance", "total:", "amount", "usd",
            "profit", "loss", "stock", "market", "trading", "crypto"
        ]),
        ["communication"] = new("Communication", 0.88f,
        [
            "email", "message", "meeting", "reply", "sent", "received", "subject:", "from:",
            "to:", "chat", "slack", "teams", "discord"
        ]),
        ["documentation"] = new("Documentation", 0.86f,
        [
            "documentation", "readme", "guide", "tutorial", "wiki", "manual", "api reference",
            "specification", "how to"
        ]),
        ["configuration"] = new("Configuration", 0.87f,
        [
            "config", "settings", ".json", ".yaml", ".xml", ".env", "connection string",
            "endpoint", "localhost", "port"
        ]),
        ["data"] = new("Data & Analytics", 0.85f,
        [
            "table", "report", "metrics", "dashboard", "statistics", "chart", "graph",
            "analytics", "query", "dataset"
        ])
    };

    private static readonly Dictionary<string, string[]> TagPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["url"] = ["http://", "https://", "www.", ".com", ".io", ".dev"],
        ["email"] = ["@gmail.", "@outlook.", "@yahoo.", "mailto:"],
        ["datetime"] = ["am", "pm", "2024-", "2025-", "2026-", "jan ", "feb ", "mar "],
        ["version"] = ["v1.", "v2.", "v3.", "version", "release", "build"],
        ["filepath"] = [".cs", ".xaml", ".py", ".js", ".ts", ".sql", ".md", ".json", "\\"]
    };

    private static readonly HashSet<string> CommonTechnicalFalsePositives = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "And", "For", "Are", "But", "Not", "You", "All", "Can", "Had", "This",
        "That", "With", "Have", "From", "They", "Will", "Would", "There", "Their",
        "What", "About", "Error", "Warning", "Info", "Debug"
    };

    public AiSemanticService()
    {
        TryInitializeModel();
    }

    public bool IsInitialized => _session is not null && _tokenizer is not null;

    public string AvailabilityState => _availabilityState;

    public async Task<AiSemanticResult> AnalyzeAsync(string text, CancellationToken token = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new AiSemanticResult();

        if (string.IsNullOrWhiteSpace(text))
        {
            result.ErrorMessage = "Empty input text";
            result.ProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds;
            return result;
        }

        token.ThrowIfCancellationRequested();

        var lowerText = text.ToLowerInvariant();
        var bestCategory = "unknown";
        var bestScore = 0f;
        var tags = new List<string>();

        foreach (var (key, profile) in CategoryProfiles)
        {
            var matches = profile.Keywords.Count(keyword => lowerText.Contains(keyword, StringComparison.Ordinal));
            if (matches == 0)
            {
                continue;
            }

            var densityBoost = Math.Min(1.0f, matches / 4f);
            var score = Math.Min(1.0f, densityBoost * profile.Boost);
            tags.Add($"#{key}");

            if (score > bestScore)
            {
                bestScore = score;
                bestCategory = profile.Name;
            }
        }

        foreach (var (tag, patterns) in TagPatterns)
        {
            if (patterns.Any(pattern => lowerText.Contains(pattern.ToLowerInvariant(), StringComparison.Ordinal)))
            {
                tags.Add($"#{tag}");
            }
        }

        tags.AddRange(ExtractTechnicalTags(text));

        if (IsInitialized)
        {
            try
            {
                result.Embeddings = await GenerateEmbeddingsAsync(text, token);
                _availabilityState = "Onnx";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _availabilityState = "HeuristicOnly";
            }
        }

        result.Success = true;
        result.PrimaryCategory = bestCategory;
        result.CategoryConfidence = bestScore;
        result.Tags = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList();
        result.Summary = BuildSummary(text);
        result.ProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds;

        return result;
    }

    private void TryInitializeModel()
    {
        var semanticFolder = Path.Combine(AppContext.BaseDirectory, "AIModels", "semantic");
        var modelPath = Path.Combine(semanticFolder, "model_quantized.onnx");
        var vocabPath = Path.Combine(semanticFolder, "vocab.txt");

        if (!File.Exists(modelPath))
        {
            modelPath = Path.Combine(semanticFolder, "model.onnx");
        }

        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            _availabilityState = "HeuristicOnly";
            return;
        }

        try
        {
            _session = new InferenceSession(modelPath);
            _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions
            {
                LowerCaseBeforeTokenization = true,
                ApplyBasicTokenization = true
            });
            _availabilityState = "Onnx";
        }
        catch
        {
            _session?.Dispose();
            _session = null;
            _tokenizer = null;
            _availabilityState = "HeuristicOnly";
        }
    }

    private async Task<float[]> GenerateEmbeddingsAsync(string text, CancellationToken token)
    {
        await _inferenceLock.WaitAsync(token);
        try
        {
            var tokenIds = _tokenizer!.EncodeToIds(text, true, true).Take(MaxTokens).ToArray();
            if (tokenIds.Length == 0)
            {
                return [];
            }

            var inputIds = new long[MaxTokens];
            var attentionMask = new long[MaxTokens];
            var tokenTypeIds = new long[MaxTokens];

            for (var i = 0; i < tokenIds.Length; i++)
            {
                inputIds[i] = tokenIds[i];
                attentionMask[i] = 1;
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [1, MaxTokens])),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [1, MaxTokens]))
            };

            if (_session!.InputMetadata.ContainsKey("token_type_ids"))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, [1, MaxTokens])));
            }

            using var results = _session.Run(inputs);
            var firstTensor = results.First().AsTensor<float>();
            return MeanPool(firstTensor, attentionMask);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private static float[] MeanPool(Tensor<float> tensor, long[] attentionMask)
    {
        var dimensions = tensor.Dimensions.ToArray();
        if (dimensions.Length == 2)
        {
            return tensor.ToArray();
        }

        if (dimensions.Length < 3)
        {
            return [];
        }

        var sequenceLength = dimensions[^2];
        var hiddenSize = dimensions[^1];
        var values = tensor.ToArray();
        var pooled = new float[hiddenSize];
        var tokenCount = 0;

        for (var tokenIndex = 0; tokenIndex < sequenceLength && tokenIndex < attentionMask.Length; tokenIndex++)
        {
            if (attentionMask[tokenIndex] == 0)
            {
                continue;
            }

            tokenCount++;
            for (var hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++)
            {
                pooled[hiddenIndex] += values[(tokenIndex * hiddenSize) + hiddenIndex];
            }
        }

        if (tokenCount == 0)
        {
            return pooled;
        }

        for (var i = 0; i < pooled.Length; i++)
        {
            pooled[i] /= tokenCount;
        }

        return pooled;
    }

    private static IEnumerable<string> ExtractTechnicalTags(string text)
    {
        foreach (Match match in Regex.Matches(text, @"\b[A-Z][a-zA-Z0-9]{2,24}\b"))
        {
            var value = match.Value;
            if (!CommonTechnicalFalsePositives.Contains(value))
            {
                yield return value;
            }
        }
    }

    private static string BuildSummary(string text)
    {
        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        if (normalized.Length <= 180)
        {
            return normalized;
        }

        return normalized[..180].TrimEnd() + "...";
    }

    private sealed record CategoryProfile(string Name, float Boost, string[] Keywords);

    public void Dispose()
    {
        _session?.Dispose();
        _inferenceLock.Dispose();
    }
}
