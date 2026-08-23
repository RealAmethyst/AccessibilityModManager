using System.Text.Json;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class SigningWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amm-signing-workflow-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Key_lifecycle_uses_the_shared_store_and_never_reports_secrets()
    {
        var workflow = CreateWorkflow(Path.Combine(_root, "first"));

        var created = workflow.Create("amethyst", "correct horse battery staple");
        var status = workflow.GetStatus("amethyst");
        var backup = Path.Combine(_root, "amethyst-key.json");
        var exported = workflow.Export("amethyst", backup, "different export secret");
        var changed = workflow.ChangePassphrase(
            "amethyst", "correct horse battery staple", "new local secret");
        var wrong = workflow.ChangePassphrase("amethyst", "not the secret", "another secret");

        Assert.Equal(WorkflowErrorKind.None, created.ErrorKind);
        Assert.Equal(created.Value!.PublicKeyFingerprint, status.Value!.PublicKeyFingerprint);
        Assert.Equal(WorkflowErrorKind.None, exported.ErrorKind);
        Assert.True(File.Exists(backup));
        Assert.Equal(WorkflowErrorKind.None, changed.ErrorKind);
        Assert.Equal(WorkflowErrorKind.Validation, wrong.ErrorKind);

        var allMessages = string.Join("\n", created.Messages
            .Concat(exported.Messages)
            .Concat(changed.Messages)
            .Concat(wrong.Messages));
        Assert.DoesNotContain("correct horse battery staple", allMessages, StringComparison.Ordinal);
        Assert.DoesNotContain("different export secret", allMessages, StringComparison.Ordinal);
        Assert.DoesNotContain("new local secret", allMessages, StringComparison.Ordinal);
        Assert.DoesNotContain("not the secret", allMessages, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_and_sign_use_the_verified_anchor_and_journal_the_exact_bytes()
    {
        var configRoot = Path.Combine(_root, "claims");
        var source = new FakeSigningCatalogSource();
        var workflow = CreateWorkflow(configRoot, source);
        var created = workflow.Create("amethyst", "local secret").Value!;
        source.RegistryJson = Registry(created);

        var project = Path.Combine(_root, "project");
        new IndexFileService(TestLogger.Create()).Save(
            project, CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex());

        var preview = await workflow.PreviewClaimsAsync(project, CancellationToken.None);
        var signed = await workflow.SignClaimsAsync(
            project, preview.Value!.DeletionsToken, confirmed: true, CancellationToken.None);
        var head = workflow.GetHeadStatus("amethyst");

        Assert.Equal(WorkflowErrorKind.None, preview.ErrorKind);
        Assert.Equal(1, preview.Value.PublishNumber);
        Assert.Equal(WorkflowErrorKind.None, signed.ErrorKind);
        Assert.NotNull(signed.Value);
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(signed.Value.IndexJson)),
            signed.Value.Pending.IndexSha256);
        Assert.NotNull(head.Value!.Single().Pending);
    }

    [Fact]
    public async Task An_imported_recordless_backup_cannot_bootstrap_a_signed_history()
    {
        var first = CreateWorkflow(Path.Combine(_root, "source"));
        var created = first.Create("amethyst", "local secret").Value!;
        var backup = Path.Combine(_root, "recordless.json");
        Assert.Equal(
            WorkflowErrorKind.None,
            first.Export("amethyst", backup, "backup secret").ErrorKind);

        var source = new FakeSigningCatalogSource { RegistryJson = Registry(created) };
        var restored = CreateWorkflow(Path.Combine(_root, "restored"), source);
        Assert.Equal(WorkflowErrorKind.None, restored.Import(backup, "backup secret").ErrorKind);

        var project = Path.Combine(_root, "restored-project");
        new IndexFileService(TestLogger.Create()).Save(
            project, CatalogWorkflowTests.CatalogFixture.CreateCompleteIndex());

        var result = await restored.SignClaimsAsync(
            project, deletionsToken: "", confirmed: true, CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.Conflict, result.ErrorKind);
        Assert.Contains("backup", string.Join(" ", result.Messages), StringComparison.OrdinalIgnoreCase);
    }

    private static SigningWorkflow CreateWorkflow(
        string configRoot,
        ISigningCatalogSource? source = null)
    {
        Directory.CreateDirectory(configRoot);
        var logger = TestLogger.Create();
        var config = new AuthorConfigService(logger, configRoot);
        var heads = new PublisherHeadStore(config, logger);
        var keys = new ClaimSigningKeyStore(config, heads, logger);
        return new SigningWorkflow(
            keys,
            heads,
            new IndexProofService(keys, heads, logger),
            new IndexFileService(logger),
            source ?? new FakeSigningCatalogSource(),
            logger);
    }

    private static string Registry(SigningKeyStatus key) => JsonSerializer.Serialize(new
    {
        registryVersion = "3",
        plugins = new[]
        {
            new
            {
                id = key.PluginId,
                repoIndexUrl = $"https://accessibilitymods.com/registry/plugins/{key.PluginId}/index.json",
                indexTrust = new
                {
                    scheme = ClaimTrustAnchor.SchemeV1,
                    keyId = key.KeyId,
                    algorithm = ClaimTrustAnchor.AlgorithmRsaPssSha256,
                    publicKeyPem = key.PublicKeyPem
                }
            }
        }
    });

    private sealed class FakeSigningCatalogSource : ISigningCatalogSource
    {
        public string RegistryJson { get; set; } = "{}";
        public byte[]? LiveIndex { get; set; }

        public Task<string> ReadVerifiedRegistryAsync(string pluginId, CancellationToken ct) =>
            Task.FromResult(RegistryJson);

        public Task<ServerUploadService.RemoteIndex> ReadLiveIndexAsync(
            string pluginId,
            CancellationToken ct) =>
            Task.FromResult(new ServerUploadService.RemoteIndex(LiveIndex is not null, LiveIndex));
    }
}
