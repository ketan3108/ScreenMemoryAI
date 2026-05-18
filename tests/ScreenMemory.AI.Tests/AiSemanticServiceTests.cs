using FluentAssertions;
using ScreenMemory.AI.App.Services;

namespace ScreenMemory.AI.Tests;

public sealed class AiSemanticServiceTests
{
    [Fact]
    public async Task AnalyzeAsyncLoadsOnnxModelAndReturnsEmbeddingWhenAssetsExist()
    {
        using var service = new AiSemanticService();

        service.AvailabilityState.Should().Be("Onnx");

        var result = await service.AnalyzeAsync("public async Task SaveInvoiceAsync() { return; }");

        result.Success.Should().BeTrue();
        result.PrimaryCategory.Should().Be("Code Snippet");
        result.Tags.Should().Contain("#code");
        result.Embeddings.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsyncDoesNotAssignFinancialCategoryFromSingleGenericWord()
    {
        using var service = new AiSemanticService();

        var result = await service.AnalyzeAsync("Total screenshots indexed in the local library");

        result.Success.Should().BeTrue();
        result.PrimaryCategory.Should().Be("unknown");
        result.Tags.Should().NotContain("#financial");
    }

    [Fact]
    public async Task AnalyzeAsyncRequiresUsefulEvidenceBeforeCategorizing()
    {
        using var service = new AiSemanticService();

        var result = await service.AnalyzeAsync("ScreenMemory Settings Back Add Folder Local Processing Only");

        result.Success.Should().BeTrue();
        result.PrimaryCategory.Should().Be("unknown");
    }
}
