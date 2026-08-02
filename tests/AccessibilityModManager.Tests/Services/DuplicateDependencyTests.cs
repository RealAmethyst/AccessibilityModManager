using AccessibilityModManager.Infrastructure.Services;
using Xunit;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// A game declaring the same dependency twice.
///
/// <para>This shipped. Pokémon TCG Live declared <c>melonloader</c> twice — once extracting into
/// <c>Updater\1.5.0</c>, once into the game root — and every install of that mod failed. The engine
/// reaches the FIRST entry, installed MelonLoader into the subfolder, then checked for
/// <c>version.dll</c> in the game root, where the SECOND entry would have put it, found nothing, and
/// aborted before the correct entry was ever reached. The error blamed the check rule, which was
/// right all along.</para>
///
/// <para>The severities here are deliberate and were the hard part. Refusing the whole index would
/// have blanked the catalog of anyone whose already-published index contained one — the recorded
/// rule that manager-side validation only tightens behind a scheme bump. Dropping it silently would
/// hide it from the only person who can fix it. So it blocks PUBLISHING, is tolerated when READING,
/// and refuses at the point it actually bites: installing.</para>
/// </summary>
public sealed class DuplicateDependencyTests
{
    [Fact]
    public void ADuplicateDependencyId_IsAnAuthoringProblem_NotATrustError()
    {
        var report = PluginIndexValidation.Validate("amethyst", IndexWithDuplicateDependency());

        // Not fatal to reading: an index already published with this must keep working.
        Assert.Empty(report.TrustErrors);
        Assert.Empty(report.UnobtainableReleases);

        // But named, so the AuthorTool's publish gate stops it.
        var problem = Assert.Single(report.AuthoringProblems);
        Assert.Contains("melonloader", problem);
        Assert.Contains("more than once", problem);
    }

    [Fact]
    public void ADuplicateDifferingOnlyInCase_IsStillADuplicate()
    {
        // Dependency ids become folder and receipt names on Windows, where two spellings are one
        // thing — so the refcount, the receipt and the install target would all collide.
        var report = PluginIndexValidation.Validate(
            "amethyst", IndexWithDuplicateDependency(secondId: "MelonLoader"));

        Assert.Single(report.AuthoringProblems);
    }

    [Fact]
    public void PublishBlockers_CarryEverySeverity()
    {
        // The AuthorTool publishes only when this is empty, so a severity missing from it is a
        // severity nothing stops the author shipping. The fixture carries one of EACH — a count
        // assertion against a report whose other lists happen to be empty proves nothing about
        // whether those lists were included.
        var report = PluginIndexValidation.Validate("amethyst", IndexWithEverySeverity());

        Assert.NotEmpty(report.TrustErrors);
        Assert.NotEmpty(report.UnobtainableReleases);
        Assert.NotEmpty(report.AuthoringProblems);

        Assert.Equal(
            report.TrustErrors.Count + report.UnobtainableReleases.Count + report.AuthoringProblems.Count,
            report.PublishBlockers.Count);
    }

    /// <summary>
    /// One of each severity: a release claiming another plugin (trust), a sha256 no download could
    /// ever match (unobtainable), and a duplicated dependency (authoring).
    /// </summary>
    private static string IndexWithEverySeverity() => """
        {
          "pluginId": "amethyst",
          "repoVersion": "1",
          "generatedAt": "2026-08-02T00:00:00Z",
          "games": [
            {
              "gameId": "tcg-live",
              "displayName": "Pokemon TCG Live",
              "dependencies": [
                { "id": "melonloader", "type": "framework", "required": true },
                { "id": "melonloader", "type": "framework", "required": true }
              ]
            }
          ],
          "releasesByGameId": {
            "tcg-live": [
              {
                "pluginId": "impostor",
                "gameId": "tcg-live",
                "version": "1.0",
                "channel": "stable",
                "packageUrl": "https://example.invalid/p.zip",
                "sha256": "not-a-hash"
              }
            ]
          }
        }
        """;

    [Fact]
    public void AnIndexWithNoDuplicates_HasNoAuthoringProblems()
    {
        var report = PluginIndexValidation.Validate(
            "amethyst", IndexWithDuplicateDependency(secondId: "bepinex"));

        Assert.Empty(report.AuthoringProblems);
        Assert.Empty(report.TrustErrors);
    }

    /// <summary>
    /// The real shape, reduced: two dependencies sharing an id, with different install targets —
    /// exactly what the live tcg-live index carried.
    /// </summary>
    private static string IndexWithDuplicateDependency(string secondId = "melonloader") => $$"""
        {
          "pluginId": "amethyst",
          "repoVersion": "1",
          "generatedAt": "2026-08-02T00:00:00Z",
          "games": [
            {
              "gameId": "tcg-live",
              "displayName": "Pokemon TCG Live",
              "dependencies": [
                {
                  "id": "melonloader",
                  "type": "framework",
                  "check": { "filePath": "version.dll" },
                  "fix": {
                    "downloadUrl": "https://example.invalid/ml.zip",
                    "autoInstall": {
                      "kind": "extractZip",
                      "targetDir": "Updater\\1.5.0",
                      "sha256": "1111111111111111111111111111111111111111111111111111111111111111"
                    }
                  },
                  "required": true
                },
                {
                  "id": "{{secondId}}",
                  "type": "framework",
                  "check": { "filePath": "version.dll" },
                  "fix": {
                    "downloadUrl": "https://example.invalid/ml.zip",
                    "autoInstall": {
                      "kind": "extractZip",
                      "sha256": "1111111111111111111111111111111111111111111111111111111111111111"
                    }
                  },
                  "required": true
                }
              ]
            }
          ],
          "releasesByGameId": {}
        }
        """;
}
