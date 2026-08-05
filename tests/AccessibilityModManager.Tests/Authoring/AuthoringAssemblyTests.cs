using AccessibilityModManager.AuthorTool.Services;

namespace AccessibilityModManager.Tests.Authoring;

/// <summary>
/// The UI-independent authoring services belong to the shared authoring assembly rather than
/// the WPF host. Their namespaces remain compatible while both the GUI and CLI consume them.
/// </summary>
public sealed class AuthoringAssemblyTests
{
    [Fact]
    public void UiIndependentServicesLiveInAuthoringAssembly()
    {
        Assert.Equal("AccessibilityModManager.Authoring",
            typeof(ManifestBuilderService).Assembly.GetName().Name);
        Assert.Equal(typeof(ManifestBuilderService).Assembly,
            typeof(IndexPublishCoordinator).Assembly);
        Assert.Equal(typeof(ManifestBuilderService).Assembly,
            typeof(ClaimSigningKeyStore).Assembly);
    }
}
