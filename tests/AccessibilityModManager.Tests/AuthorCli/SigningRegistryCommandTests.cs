using AccessibilityModManager.AuthorCli;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Tests.Helpers;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class SigningRegistryCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amm-signing-registry-cli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Signing_create_reads_its_passphrase_from_stdin_without_echoing_it()
    {
        var result = await InvokeAsync(
            "super private signing secret\n",
            "--json", "--quiet", "signing", "create", "--plugin", "amethyst", "--passphrase-stdin");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);
        Assert.Contains("publicKeyFingerprint", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super private signing secret", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("super private signing secret", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registry_group_is_discoverable_but_standard_build_refuses_with_exit_4()
    {
        var help = await InvokeAsync(string.Empty, "registry", "--help");
        var status = await InvokeAsync(string.Empty, "--json", "--quiet", "registry", "status");

        Assert.Equal((int)CliExitCode.Success, help.ExitCode);
        Assert.Contains("json", help.Stdout, StringComparison.OrdinalIgnoreCase);
#if REGISTRY_ADMIN
        Assert.True(AuthoringBuildFlags.IsRegistryAdmin);
        Assert.Equal((int)CliExitCode.Success, status.ExitCode);
#else
        Assert.False(AuthoringBuildFlags.IsRegistryAdmin);
        Assert.Equal((int)CliExitCode.Authentication, status.ExitCode);
        Assert.Contains("admin build", status.Stdout + status.Stderr, StringComparison.OrdinalIgnoreCase);
#endif
    }

    [Fact]
    public async Task Registry_mutation_handlers_check_the_admin_build_before_confirmation()
    {
        var commit = await InvokeAsync(
            string.Empty,
            "--json", "--quiet", "registry", "commit", "--repo", _root);
        var push = await InvokeAsync(
            string.Empty,
            "--json", "--quiet", "registry", "push", "--repo", _root);

#if REGISTRY_ADMIN
        Assert.Equal((int)CliExitCode.Conflict, commit.ExitCode);
        Assert.Equal((int)CliExitCode.Conflict, push.ExitCode);
#else
        Assert.Equal((int)CliExitCode.Authentication, commit.ExitCode);
        Assert.Equal((int)CliExitCode.Authentication, push.ExitCode);
        Assert.Contains("admin build", commit.Stdout + commit.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("admin build", push.Stdout + push.Stderr, StringComparison.OrdinalIgnoreCase);
#endif
    }

    private async Task<CliRunResult> InvokeAsync(string stdin, params string[] args)
    {
        Directory.CreateDirectory(_root);
        using var input = new StringReader(stdin);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var console = new TestCliConsole(input, output, error);
        using var services = CliServices.Create(new CliServiceOverrides(
            Console: console,
            Logger: TestLogger.Create(),
            AuthorConfigDirectory: Path.Combine(_root, "config"),
            LogDirectory: Path.Combine(_root, "logs")));

        var exitCode = await Program.RunAsync(args, services);
        return new CliRunResult(exitCode, output.ToString(), error.ToString());
    }

    private sealed class TestCliConsole(TextReader input, TextWriter output, TextWriter error) : ICliConsole
    {
        public TextReader In { get; } = input;
        public TextWriter Out { get; } = output;
        public TextWriter Error { get; } = error;
        public bool IsInputRedirected => true;
        public void WriteStatus(string message) => Error.WriteLine(message);
    }

    private sealed record CliRunResult(int ExitCode, string Stdout, string Stderr);
}
