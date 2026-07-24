using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Installer;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.Installer;

public class InstallVerifierTests : IDisposable
{
    private readonly string _gameDir;
    private readonly InstallVerifier _verifier;

    public InstallVerifierTests()
    {
        _gameDir = Path.Combine(Path.GetTempPath(), "ammtest_verify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_gameDir);
        _verifier = new InstallVerifier(TestLogger.Create());
    }

    public void Dispose()
    {
        try { Directory.Delete(_gameDir, true); } catch { }
    }

    [Fact]
    public void Verify_FileExistsRuleInsideGame_Passes()
    {
        File.WriteAllText(Path.Combine(_gameDir, "present.dll"), "x");
        var rules = new List<VerifyRule> { new() { Type = "fileExists", Path = "present.dll" } };
        Assert.True(_verifier.Verify(rules, _gameDir));
    }

    [Fact]
    public void Verify_RulePathEscapingGameFolder_FailsEvenWhenTheOutsideFileExists()
    {
        // A verify rule that resolves outside the game folder must fail the rule, never satisfy it
        // by probing an arbitrary external file. We create a real file outside and point an
        // escaping rule straight at it — it must still fail.
        var outsideDir = Path.Combine(Path.GetTempPath(), "ammtest_verify_out_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        try
        {
            File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "x");
            var escapingRelPath = Path.Combine("..", Path.GetFileName(outsideDir), "secret.txt");
            var rules = new List<VerifyRule> { new() { Type = "fileExists", Path = escapingRelPath } };

            Assert.False(_verifier.Verify(rules, _gameDir));
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch { }
        }
    }
}
