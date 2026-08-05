using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.App.ViewModels;

/// <summary>
/// The text of the risk notice shown before a source is added.
///
/// <para>This wording is the whole of the protection at add time, so it says what a source can
/// actually DO rather than that it "may be unsafe". A vague warning teaches people to click through
/// warnings; a specific one gives them something to decide with. It names install, setup programs
/// and administrator access because those are the real capabilities — a source gets exactly the
/// access the built-in mods have, which is the settled design, not an oversight.</para>
///
/// <para>It also says what adding does NOT do. "Adding installs nothing now" is true and matters:
/// without it, a cautious reader has to assume the worst about a button they have not pressed yet.</para>
/// </summary>
public sealed class SourceRiskDialogViewModel(SourcePreview preview)
{
    public string Headline => "Add a source you trust?";

    public string Summary =>
        $"{preview.DisplayName} — {Mods(preview.GameCount)}.";

    public string RiskText =>
        "Anyone can publish a source. Nobody has checked this one, and it is not connected to " +
        "Amethyst's own mods.\n" +
        "\n" +
        "If you later install a mod from this source, it can put files into your game folders, run " +
        "setup programs, and ask Windows for administrator permission. A program running as " +
        "administrator can change any part of this computer. That is the same access the built-in " +
        "mods have.\n" +
        "\n" +
        "Adding a source does not install anything now, and the manager will still ask you before " +
        "it runs a setup program or a script.\n" +
        "\n" +
        "Only add this source if you trust the person who gave you the address.\n" +
        "\n" +
        $"Developer name: {preview.DisplayName}\n" +
        $"Developer id: {preview.PluginId}\n" +
        $"Address: {preview.IndexUrl}";

    private static string Mods(int count) =>
        count == 1 ? "1 mod" : $"{count} mods";
}
