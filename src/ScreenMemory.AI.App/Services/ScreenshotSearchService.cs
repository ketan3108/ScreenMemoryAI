using ScreenMemory.AI.Core.Data;
using ScreenMemory.AI.Core.Models;
using ScreenMemory.AI.Core.Services;

namespace ScreenMemory.AI.App.Services;

public static class ScreenshotSearchService
{
    public static async Task<List<ScreenshotRecord>> SearchAsync(
        ScreenshotRepository repository,
        IAiSemanticService semanticService,
        string query,
        int limit,
        SearchMode mode,
        CancellationToken token = default)
    {
        const string semanticPrefix = "semantic:";
        var trimmedQuery = query.Trim();
        var hasSemanticPrefix = trimmedQuery.StartsWith(semanticPrefix, StringComparison.OrdinalIgnoreCase);
        var searchText = hasSemanticPrefix
            ? trimmedQuery[semanticPrefix.Length..].Trim()
            : trimmedQuery;

        if (searchText.Length == 0)
        {
            return [];
        }

        if (mode == SearchMode.Ai || hasSemanticPrefix)
        {
            if (!semanticService.IsInitialized)
            {
                return await Task.Run(() => repository.SearchHybrid(searchText, limit), token);
            }

            var semanticResult = await semanticService.AnalyzeAsync(searchText, token);
            if (semanticResult.Embeddings.Length == 0)
            {
                return await Task.Run(() => repository.SearchHybrid(searchText, limit), token);
            }

            return await Task.Run(() => repository.SearchByEmbedding(semanticResult.Embeddings, limit), token);
        }

        return await Task.Run(() => repository.SearchHybrid(searchText, limit), token);
    }
}
