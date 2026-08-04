using AccessibilityModManager.AuthorTool.ViewModels;
using AccessibilityModManager.Core.Models;
using Xunit;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// The .NET 9 presets, pinned against the shape a real install actually has.
///
/// <para>These exist because every field in them is a thing that fails silently when wrong: a
/// mistyped key path reports the runtime missing forever, the wrong architecture installs a runtime
/// the game cannot load, and a hash that isn't the installer's turns every install into a refusal
/// after a 60 MB download.</para>
/// </summary>
public class Net9PresetTests
{
    private static Dependency Preset(string displayName) =>
        DependencyPresets.All.Single(p => p.DisplayName == displayName).ToModel();

    private static Dependency X64 => Preset(".NET 9 Desktop Runtime (64-bit)");
    private static Dependency X86 => Preset(".NET 9 Desktop Runtime (32-bit)");

    [Fact]
    public void TheTwoArchitecturesAreSeparateDependencies()
    {
        // One id per entry. Sharing one would make the second unreachable — the id keys both the
        // refcount receipt and the post-install re-check, which is what broke the TCG Live install.
        Assert.NotEqual(X64.Id, X86.Id);
        Assert.Equal("dotnet-9-desktop-x64", X64.Id);
        Assert.Equal("dotnet-9-desktop-x86", X86.Id);
    }

    /// <summary>
    /// The architecture lives in the KEY PATH, not the registry view — both records sit under
    /// WOW6432Node, which the checker reaches because it probes both views by default. Pinning a
    /// view here would break it.
    /// </summary>
    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    public void TheCheckPointsAtTheRuntimesOwnRecord(string arch)
    {
        var dep = arch == "x64" ? X64 : X86;

        Assert.Equal(
            $@"SOFTWARE\dotnet\Setup\InstalledVersions\{arch}\sharedfx\Microsoft.WindowsDesktop.App",
            dep.Check!.RegistryKey);

        // No value name: the installed versions ARE the value names, and naming one would ask for a
        // value that does not exist. No view pin: see above.
        Assert.Null(dep.Check.RegistryValue);
        Assert.Null(dep.Check.RegistryView);
        Assert.Equal("9.0.0", dep.MinVersion);
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    public void TheInstallerIsPinnedHttpsAndMatchesItsArchitecture(string arch)
    {
        var dep = arch == "x64" ? X64 : X86;
        var url = dep.Fix!.DownloadUrl!;

        Assert.StartsWith("https://", url);
        Assert.Contains($"win-{arch}.exe", url);
        // Pinned to an exact patch: the SHA256 gate is absolute, so an "always latest" address
        // would start failing the day Microsoft ships the next one.
        Assert.Contains("9.0.18", url);
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    public void TheInstallerRunsSilentlyAndIsHashPinned(string arch)
    {
        var dep = arch == "x64" ? X64 : X86;
        var install = Assert.IsType<RunInstallerAutoInstall>(dep.Fix!.AutoInstall);

        Assert.Equal(64, install.Sha256.Length);
        Assert.True(install.Sha256.All(Uri.IsHexDigit), "the hash must be hex");
        Assert.True(install.NeedsAdmin, "the .NET installer needs elevation");

        // /norestart in particular: without it the installer can reboot the machine mid-install.
        Assert.Equal(new[] { "/install", "/quiet", "/norestart" }, install.Args);
    }

    [Fact]
    public void TheTwoPresetsDoNotShareAHashOrAnUrl()
    {
        // A copy-paste slip here would install the wrong architecture while looking correct.
        var a = (RunInstallerAutoInstall)X64.Fix!.AutoInstall!;
        var b = (RunInstallerAutoInstall)X86.Fix!.AutoInstall!;

        Assert.NotEqual(a.Sha256, b.Sha256);
        Assert.NotEqual(X64.Fix.DownloadUrl, X86.Fix.DownloadUrl);
    }
}
