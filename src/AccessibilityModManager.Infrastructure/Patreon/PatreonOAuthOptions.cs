namespace AccessibilityModManager.Infrastructure.Patreon;

/// <summary>
/// Per-app OAuth configuration. The manager and the AuthorTool both use the same
/// <see cref="PatreonClient"/> code paths but with different client IDs / scopes / loopback
/// ports — keeping them as a config struct lets each project plug in its own values without
/// duplicating the protocol code.
/// </summary>
public sealed record PatreonOAuthOptions(
    string ClientId,
    int LoopbackPort,
    IReadOnlyList<string> Scopes)
{
    /// <summary>The exact redirect URI we register on Patreon and pass on every authorize request.</summary>
    public string RedirectUri => $"http://127.0.0.1:{LoopbackPort}/callback";
}

/// <summary>
/// Constants for the two Patreon OAuth apps registered for this project. See
/// PATREON_OAUTH_REGISTRATION.md for the registration walkthrough.
/// </summary>
public static class PatreonAppRegistry
{
    /// <summary>
    /// Manager (patron-side) sign-in. Reads tier membership AND the user's own campaign list
    /// so the manager can detect when the signed-in user is the creator of a gated release's
    /// campaign — that triggers the local-file install path instead of trying to download
    /// (Patreon returns no URL to creators viewing their own paid posts).
    /// </summary>
    public static readonly PatreonOAuthOptions Manager = new(
        ClientId: "z1RFuf723vUD-8o3crPxNOmxQqNBV0Hvp80neWGHHQPqlBEjC_T1evXyeEeCZc2x",
        LoopbackPort: 53682,
        Scopes: new[] { "identity", "identity[email]", "campaigns" });

    /// <summary>
    /// AuthorTool (creator-side) sign-in. Identity + campaigns so the tool can fetch the
    /// author's own tier list, plus <c>campaigns.posts</c> so it can read a post by id when
    /// the author pastes a URL during release validation. Bare <c>campaigns</c> is
    /// insufficient — Patreon's v2 API rejects <c>/posts/{id}</c> without the dotted child
    /// scope.
    /// </summary>
    public static readonly PatreonOAuthOptions Author = new(
        ClientId: "xsYebNVhUQpzifnzKp7zLbChbYRY5fRbGM2Fr2JzByLVorIHNvqe4ap9rAApfZTP",
        LoopbackPort: 53683,
        Scopes: new[] { "identity", "identity[email]", "campaigns", "campaigns.posts" });
}
