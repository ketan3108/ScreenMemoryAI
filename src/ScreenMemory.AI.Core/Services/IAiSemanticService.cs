namespace ScreenMemory.AI.Core.Services;

public sealed class AiSemanticResult
{
    public bool Success { get; set; }

    public string PrimaryCategory { get; set; } = "unknown";

    public float CategoryConfidence { get; set; }

    public List<string> Tags { get; set; } = [];

    public float[] Embeddings { get; set; } = [];

    public string Summary { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public double ProcessingTimeMs { get; set; }

    public string TagsAsString => string.Join(", ", Tags.Distinct(StringComparer.OrdinalIgnoreCase));
}

public interface IAiSemanticService
{
    bool IsInitialized { get; }

    string AvailabilityState { get; }

    Task<AiSemanticResult> AnalyzeAsync(string text, CancellationToken token = default);
}
