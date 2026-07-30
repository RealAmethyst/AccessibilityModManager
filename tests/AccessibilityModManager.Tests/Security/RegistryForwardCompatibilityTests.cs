using AccessibilityModManager.Infrastructure.Services;
using Xunit;

namespace AccessibilityModManager.Tests.Security;

/// <summary>
/// The registry is the one document with no per-item tolerance: a manager accepts it whole or
/// refuses it whole. So anything ADDED to it has to be provably invisible to managers that predate
/// the addition — and that has to be a test, because the moment a registry naming a signing key is
/// signed and published, every deployed manager reads it. If they refused it, every user's catalog
/// would go dark at once and no later publish could undo it: the broken document is already live and
/// the fix is another publish those same managers cannot read.
///
/// <para>This pins the precondition for anchoring an index-signing key in the registry. It is
/// deliberately written against the manager's OWN acceptance path rather than against a
/// <c>JsonSerializerOptions</c> literal, so it keeps holding if that path is rewritten.</para>
/// </summary>
public sealed class RegistryForwardCompatibilityTests
{
    /// <summary>A registry entry carrying the index-signing anchor that stage A will publish.</summary>
    private const string RegistryWithIndexTrust = """
    {
      "registryVersion": "3",
      "updatedAt": "2026-07-30T00:00:00Z",
      "plugins": [
        {
          "id": "amethyst",
          "name": "Amethyst's mods",
          "author": "Amethyst",
          "description": "Accessibility mods.",
          "repoIndexUrl": "https://accessibilitymods.com/registry/plugins/amethyst/index.json",
          "indexTrust": {
            "scheme": "signed-claims-v1",
            "keyId": "amethyst-2026-07",
            "algorithm": "rsa-pss-sha256",
            "publicKeyPem": "-----BEGIN PUBLIC KEY-----\nMIIB\n-----END PUBLIC KEY-----"
          }
        }
      ]
    }
    """;

    [Fact]
    public void A_registry_naming_an_index_signing_key_is_still_accepted_whole()
    {
        // If this ever fails, publishing the anchored registry would take every user's catalog down
        // with a perfectly valid signature over it. Nothing about the entry may become fatal just
        // because a member was added that older managers do not know about.
        var report = PluginRegistryValidation.Validate(RegistryWithIndexTrust);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
    }

    [Fact]
    public void A_published_index_carrying_a_signature_block_is_still_readable()
    {
        // Live since 30 July 2026: the published index now carries a `proof` member that managers
        // released before signing existed know nothing about. If they refused it, every user of
        // those versions would lose the catalog — so this is pinned against the manager's own
        // validation rather than left as a property of a JsonSerializerOptions literal somewhere.
        var index = """
        {
          "pluginId": "amethyst",
          "repoVersion": "1",
          "generatedAt": "2026-07-30T00:00:00Z",
          "games": [
            { "gameId": "game1", "displayName": "Game one", "modName": "Mod" }
          ],
          "releasesByGameId": {
            "game1": [
              {
                "gameId": "game1",
                "pluginId": "amethyst",
                "version": "1.0.0",
                "channel": "stable",
                "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "packageUrl": "https://accessibilitymods.com/releases/game1-1.0.0.zip"
              }
            ]
          },
          "proof": {
            "scheme": "signed-claims-v1",
            "keyId": "amethyst-2026-07",
            "algorithm": "rsa-pss-sha256",
            "claims": [],
            "manifest": "not-inspected-here"
          }
        }
        """;

        var report = AccessibilityModManager.Infrastructure.Services.PluginIndexValidation
            .Validate("amethyst", index);

        Assert.Empty(report.TrustErrors);
        Assert.Empty(report.UnobtainableReleases);
    }

    [Fact]
    public void An_unknown_member_anywhere_in_the_registry_is_ignored_rather_than_fatal()
    {
        // The same property stated generally, so it is not only 'indexTrust' that is safe to add.
        // Managers are on whatever version they are on, and the registry has to stay readable by all
        // of them for the ecosystem to be extendable at all.
        var withFutureMembers = """
        {
          "registryVersion": "3",
          "updatedAt": "2026-07-30T00:00:00Z",
          "somethingAddedLater": { "nested": [1, 2, 3] },
          "plugins": [
            {
              "id": "amethyst",
              "name": "Amethyst's mods",
              "author": "Amethyst",
              "description": "Accessibility mods.",
              "repoIndexUrl": "https://accessibilitymods.com/registry/plugins/amethyst/index.json",
              "aFieldFromTheFuture": "ignored"
            }
          ]
        }
        """;

        var report = PluginRegistryValidation.Validate(withFutureMembers);

        Assert.True(report.IsValid, string.Join("\n", report.Errors));
    }
}
