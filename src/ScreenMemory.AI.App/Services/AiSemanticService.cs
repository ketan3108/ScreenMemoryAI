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
    private const float MinimumCategoryConfidence = 0.58f;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private string _availabilityState = "HeuristicOnly";

    private static readonly Dictionary<string, CategoryProfile> CategoryProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = new("Code Snippet", 1.8f, 2,
        [
            new("function", 0.55f), new("class ", 0.65f), new("public ", 0.6f), new("private ", 0.6f),
            new("async ", 0.55f), new("await ", 0.55f), new("const ", 0.45f), new("var ", 0.35f),
            new("let ", 0.4f), new("=>", 0.7f), new("namespace", 0.75f), new("interface ", 0.75f),
            new("return ", 0.5f), new("void ", 0.55f), new("string ", 0.45f), new("bool ", 0.45f),
            new("using ", 0.45f), new("import ", 0.65f), new("from ", 0.35f), new(".cs", 0.85f),
            new(".xaml", 0.85f), new(".ts", 0.85f), new(".js", 0.85f), new("task ", 0.55f),
            new("() {", 0.75f), new("```", 0.9f)
        ]),
        ["error"] = new("Error Log", 1.55f, 2,
        [
            new("exception", 0.95f), new("error:", 0.85f), new("failed", 0.55f), new("stack trace", 1.0f),
            new("traceback", 1.0f), new("timeout", 0.55f), new("unhandled", 0.8f), new("404", 0.5f),
            new("500", 0.5f), new("cannot ", 0.35f), new("invalid ", 0.35f), new("undefined", 0.65f),
            new("nullreference", 0.95f), new("exit code", 0.7f)
        ]),
        ["financial"] = new("Financial Data", 1.55f, 2,
        [
            new("invoice", 1.0f), new("receipt", 1.0f), new("payment", 0.75f), new("balance", 0.65f),
            new("total:", 0.55f), new("amount", 0.55f), new("amount due", 0.9f), new("subtotal", 0.75f),
            new("tax", 0.45f), new("usd", 0.55f), new("$", 0.75f), new("profit", 0.65f),
            new("loss", 0.55f), new("stock", 0.45f), new("trading", 0.65f), new("crypto", 0.65f)
        ]),
        ["communication"] = new("Communication", 1.45f, 2,
        [
            new("email", 0.7f), new("message", 0.55f), new("meeting", 0.75f), new("reply", 0.55f),
            new("sent", 0.35f), new("received", 0.35f), new("subject:", 0.8f), new("from:", 0.55f),
            new("to:", 0.35f), new("chat", 0.65f), new("slack", 0.85f), new("teams", 0.75f),
            new("discord", 0.85f), new("whatsapp", 0.85f)
        ]),
        ["documentation"] = new("Documentation", 1.35f, 2,
        [
            new("documentation", 0.9f), new("readme", 0.9f), new("guide", 0.55f), new("tutorial", 0.75f),
            new("wiki", 0.75f), new("manual", 0.65f), new("api reference", 0.9f),
            new("specification", 0.75f), new("how to", 0.65f)
        ]),
        ["configuration"] = new("Configuration", 1.35f, 1,
        [
            new("config", 0.85f), new("settings", 0.55f), new(".json", 0.9f), new(".yaml", 0.9f),
            new(".yml", 0.9f), new(".xml", 0.75f), new(".env", 1.0f), new("connection string", 0.95f),
            new("endpoint", 0.65f), new("localhost", 0.75f), new("port:", 0.55f), new("appsettings", 1.0f)
        ]),
        ["data"] = new("Data & Analytics", 1.45f, 2,
        [
            new("table", 0.45f), new("report", 0.55f), new("metrics", 0.8f), new("dashboard", 0.8f),
            new("statistics", 0.75f), new("chart", 0.65f), new("graph", 0.65f),
            new("analytics", 0.9f), new("query", 0.45f), new("dataset", 0.8f)
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
            var score = ScoreCategory(lowerText, profile);
            if (score < MinimumCategoryConfidence)
            {
                continue;
            }

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
        result.PrimaryCategory = bestScore >= MinimumCategoryConfidence ? bestCategory : "unknown";
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

    private static float ScoreCategory(string lowerText, CategoryProfile profile)
    {
        var total = 0f;
        var matches = 0;
        var hasStrongSignal = false;

        foreach (var keyword in profile.Keywords)
        {
            if (!lowerText.Contains(keyword.Pattern, StringComparison.Ordinal))
            {
                continue;
            }

            matches++;
            total += keyword.Weight;
            hasStrongSignal |= keyword.Weight >= 0.85f;
        }

        if (matches == 0 || (matches < profile.MinimumMatches && !hasStrongSignal))
        {
            return 0f;
        }

        return Math.Min(0.96f, total / profile.ScoreTarget);
    }

    private sealed record CategoryProfile(string Name, float ScoreTarget, int MinimumMatches, WeightedKeyword[] Keywords);

    private sealed record WeightedKeyword(string Pattern, float Weight);

    public void Dispose()
    {
        _session?.Dispose();
        _inferenceLock.Dispose();
    }
}
