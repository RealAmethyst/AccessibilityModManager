using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class RegistryAdminWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amm-registry-workflow-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Registry_operations_are_build_gated_before_private_configuration_is_read()
    {
        Directory.CreateDirectory(_root);
        var workflow = CreateWorkflow();
        var missing = Path.Combine(_root, "does-not-exist.json");

        var result = workflow.Validate(missing);

#if REGISTRY_ADMIN
        Assert.True(AuthoringBuildFlags.IsRegistryAdmin);
        Assert.Equal(WorkflowErrorKind.Validation, result.ErrorKind);
        Assert.Contains("does-not-exist", string.Join(" ", result.Messages), StringComparison.OrdinalIgnoreCase);
#else
        Assert.False(AuthoringBuildFlags.IsRegistryAdmin);
        Assert.Equal(WorkflowErrorKind.Authentication, result.ErrorKind);
        Assert.Contains("admin build", string.Join(" ", result.Messages), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "config.json")));
#endif
    }

    [Fact]
    public void Admin_build_validates_with_the_same_rules_as_the_manager()
    {
#if REGISTRY_ADMIN
        var path = Path.Combine(_root, "plugin-registry.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path,
            """
            {
              "registryVersion": "3",
              "updatedAt": "2026-08-04T00:00:00Z",
              "plugins": []
            }
            """);

        var result = CreateWorkflow().Validate(path);

        Assert.Equal(WorkflowErrorKind.None, result.ErrorKind);
        Assert.False(result.Value!.SignaturePresent);
        Assert.Equal(path, result.Value.Path);
#else
        Assert.False(AuthoringBuildFlags.IsRegistryAdmin);
#endif
    }

    private RegistryAdminWorkflow CreateWorkflow()
    {
        var logger = TestLogger.Create();
        var config = new AuthorConfigService(logger, _root);
        return new RegistryAdminWorkflow(
            config,
            new GitService(logger),
            new ServerUploadService(logger),
            new HttpClient(),
            logger);
    }
}
