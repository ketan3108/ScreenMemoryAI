using FluentAssertions;
using ScreenMemory.AI.App.Services;

namespace ScreenMemory.AI.Tests;

public sealed class LicenseServiceTests
{
    [Fact]
    public void FreeLicenseAllowsOnlyFirstTwoHundredScreenshots()
    {
        var service = new LicenseService(licensePath: Path.Combine(
            Path.GetTempPath(),
            "ScreenMemoryAI.Tests",
            $"{Guid.NewGuid():N}.lic"));

        service.CanIndexNewScreenshot(199).Should().BeTrue();
        service.CanIndexNewScreenshot(200).Should().BeFalse();
    }

    [Fact]
    public void FreeLicenseUsageLabelShowsCapacity()
    {
        var service = new LicenseService(licensePath: Path.Combine(
            Path.GetTempPath(),
            "ScreenMemoryAI.Tests",
            $"{Guid.NewGuid():N}.lic"));

        service.GetUsageLabel(42).Should().Be("42/200 free screenshots");
    }
}
