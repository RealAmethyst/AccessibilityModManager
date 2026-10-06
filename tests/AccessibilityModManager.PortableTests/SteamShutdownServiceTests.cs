using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.PortableTests;

public sealed class SteamShutdownServiceTests
{
    [Fact]
    public async Task StopAsync_WhenSteamIsClosed_DoesNotSendShutdown()
    {
        var requested = false;
        var service = new SteamShutdownService(() => false, _ => requested = true);

        await service.StopAsync("/home/user/.local/share/Steam");

        Assert.False(requested);
    }

    [Fact]
    public async Task StopAsync_WaitsForSteamToExitAfterRequest()
    {
        var running = true;
        var requestedRoot = "";
        var service = new SteamShutdownService(() => running, root =>
        {
            requestedRoot = root;
            running = false;
        });

        await service.StopAsync("/home/user/.local/share/Steam");

        Assert.Equal("/home/user/.local/share/Steam", requestedRoot);
    }

    [Fact]
    public async Task StopAsync_WhenSteamStaysOpen_StopsBeforeInstallCanContinue()
    {
        var requested = false;
        var service = new SteamShutdownService(() => true, _ => requested = true);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.StopAsync("/home/user/.local/share/Steam", TimeSpan.Zero));

        Assert.True(requested);
    }
}
