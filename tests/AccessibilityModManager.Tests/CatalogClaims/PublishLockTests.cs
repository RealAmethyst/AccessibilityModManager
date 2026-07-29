using System.Text;
using AccessibilityModManager.AuthorTool.Services;

namespace AccessibilityModManager.Tests.CatalogClaims;

/// <summary>
/// The publish lock's decidable half: where the lock file goes, whether that place is safe, and what
/// counts as our lock. The SFTP calls around it need a live server; these rules do not, and they are
/// the ones where a mistake is quiet.
/// </summary>
public class PublishLockTests
{
    private const string CatalogRoot = "/var/www/accessibilitymods.com/registry";
    private const string ReleasesRoot = "/var/www/downloads.accessibilitymods.com/releases";

    private static string Resolve(string? configured, string? home = "/home/ola") =>
        PublishLock.ResolveRoot(configured, home, CatalogRoot, ReleasesRoot);

    // ---- where the lock lives ----

    [Fact]
    public void Unconfigured_root_lands_under_the_ssh_home()
    {
        Assert.Equal("/home/ola/.amm-publish-locks", Resolve(configured: null));
        Assert.Equal("/home/ola/.amm-publish-locks", Resolve(configured: "   "));
    }

    [Fact]
    public void Home_that_is_not_absolute_is_refused_rather_than_guessed()
    {
        // realpath(".") should come back absolute. When it doesn't, the server has not told us where
        // we are, and a guessed home is how a lock ends up somewhere nobody looks — or somewhere served.
        foreach (var home in new[] { null, "", "   ", "ola", "./ola" })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Resolve(configured: null, home));
            Assert.Contains("absolute home directory", ex.Message);
        }
    }

    [Fact]
    public void Configured_root_must_be_absolute()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve("locks/publishing"));
        Assert.Contains("not an absolute path", ex.Message);
    }

    [Theory]
    [InlineData(CatalogRoot)]                       // exactly a served root
    [InlineData(CatalogRoot + "/locks")]            // inside one
    [InlineData(CatalogRoot + "/locks/")]           // ...with a trailing slash
    [InlineData(CatalogRoot + "//locks")]           // ...spelled with a doubled separator
    [InlineData(ReleasesRoot + "/locks")]           // inside the other one
    public void A_root_the_web_server_hands_out_is_refused(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(configured));
        Assert.Contains("hands out", ex.Message);
    }

    [Fact]
    public void A_sibling_of_a_served_root_is_refused_because_the_vhost_serves_the_parent()
    {
        // /var/www/accessibilitymods.com/locks is not inside the registry folder, and is served all
        // the same: the registry is a subdirectory of the vhost root, and nginx hands out the whole
        // of it with try_files. Judging only the leaf would call this safe.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Resolve("/var/www/accessibilitymods.com/locks"));
        Assert.Contains("hands out", ex.Message);
    }

    [Fact]
    public void A_name_that_merely_shares_a_prefix_is_still_allowed()
    {
        // The containment check compares on segment boundaries, so a folder whose NAME starts like a
        // served one is not mistaken for a child of it. Two levels up from the vhost, so the parent
        // rule does not catch it either.
        Assert.Equal("/var/www-locks", Resolve("/var/www-locks"));
    }

    [Fact]
    public void An_ancestor_of_the_vhost_is_allowed()
    {
        // Nothing directly inside /var/www is served: the vhosts have their own roots below it, so a
        // lock file written here is not reachable over HTTP. The check is about disclosure, not
        // about whether a path is sensible — '/' passes for the same reason, and then fails on the
        // server for the reason it should, which is that nobody may write there.
        Assert.Equal("/var/www", Resolve("/var/www"));
        Assert.Equal("/", Resolve("/"));
    }

    [Fact]
    public void The_default_lock_folder_is_outside_everything_the_site_serves()
    {
        // The deployment this ships against: the vhost roots are under /var/www, the SSH account's
        // home is not.
        Assert.Equal("/home/ola/.amm-publish-locks", Resolve(configured: null, home: "/home/ola"));
    }

    [Fact]
    public void A_root_containing_dot_dot_is_refused()
    {
        // This is the dangerous direction: it reads as outside every served folder and resolves to
        // inside one. Rewriting it here would mean guessing at symlinks we cannot see.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Resolve("/home/ola/../var/www/accessibilitymods.com/registry/locks"));
        Assert.Contains("'..'", ex.Message);
    }

    [Fact]
    public void Spellings_of_one_directory_normalise_to_the_same_answer()
    {
        Assert.Equal("/home/ola/locks", Resolve("/home/ola/locks/"));
        Assert.Equal("/home/ola/locks", Resolve("//home//ola//locks//"));
        Assert.Equal("/home/ola/locks", Resolve("  /home/ola/locks  "));
    }

    [Fact]
    public void Containment_handles_the_filesystem_root()
    {
        Assert.True(PublishLock.IsInside("/", "/anything"));
        Assert.True(PublishLock.IsInside("/", "/"));
        Assert.True(PublishLock.IsInside("/a/b", "/a/b"));
        Assert.True(PublishLock.IsInside("/a/b", "/a/b/c"));
        Assert.False(PublishLock.IsInside("/a/b", "/a/bc"));
        Assert.False(PublishLock.IsInside("/a/b/c", "/a/b"));
    }

    // ---- the file name ----

    [Theory]
    [InlineData("amethyst")]
    [InlineData("a")]
    [InlineData("some.plugin_id-2")]
    [InlineData("0")]
    public void A_plain_plugin_id_becomes_a_lock_file(string pluginId) =>
        Assert.Equal($"{pluginId}.lock", PublishLock.FileNameFor(pluginId));

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("/etc/passwd")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".hidden")]        // a leading dot marks staged temporaries elsewhere
    [InlineData("-flag")]
    [InlineData("a b")]
    [InlineData("plugin ")]        // trailing space
    [InlineData("plug\0in")]       // a control character, which a screen reader announces as nothing
    [InlineData("plügin")]    // an allowlist, so non-ASCII is out even though it is harmless
    public void Anything_but_the_allowlist_is_refused(string pluginId) =>
        Assert.Throws<InvalidOperationException>(() => PublishLock.FileNameFor(pluginId));

    [Fact]
    public void Plugin_ids_are_length_bounded_at_both_ends()
    {
        Assert.Equal(new string('a', 64) + ".lock", PublishLock.FileNameFor(new string('a', 64)));
        Assert.Throws<InvalidOperationException>(() => PublishLock.FileNameFor(new string('a', 65)));
    }

    // ---- the body ----

    /// <summary>A well-formed token: 64 lowercase hex, the shape this tool writes.</summary>
    private const string GoodToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static PublishLockBody? Parse(byte[]? bytes, string pluginId = "a") =>
        PublishLock.TryParse(bytes, pluginId);

    [Fact]
    public void A_body_round_trips()
    {
        var body = PublishLock.NewBody("amethyst");
        var parsed = PublishLock.TryParse(PublishLock.Serialize(body), "amethyst");

        Assert.NotNull(parsed);
        Assert.Equal(body.PluginId, parsed.PluginId);
        Assert.Equal(body.Token, parsed.Token);
        Assert.Equal(body.Machine, parsed.Machine);
        Assert.Equal(body.TakenAtUtc, parsed.TakenAtUtc);
    }

    [Fact]
    public void Each_acquisition_gets_its_own_token()
    {
        var first = PublishLock.NewBody("amethyst");
        var second = PublishLock.NewBody("amethyst");

        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(64, first.Token.Length); // 32 random bytes, lowercase hex
    }

    [Fact]
    public void A_body_that_is_not_a_lock_reads_as_none_rather_than_throwing()
    {
        // Reporting rather than throwing is the point: a lock we cannot understand is still a lock
        // we must not delete, and that answer has to survive being asked for.
        Assert.Null(Parse(null));
        Assert.Null(Parse([]));
        Assert.Null(Parse(Encoding.UTF8.GetBytes("not json at all")));
        Assert.Null(Parse(Encoding.UTF8.GetBytes("{}")));
        Assert.Null(Parse(Encoding.UTF8.GetBytes("[]")));
    }

    [Fact]
    public void An_oversized_lock_is_refused_without_being_read()
    {
        var huge = Encoding.UTF8.GetBytes(
            "{\"v\":1,\"pluginId\":\"a\",\"machine\":\"m\",\"user\":\"u\",\"takenAtUtc\":\"x\",\"token\":\"" +
            new string('a', PublishLock.MaxBodyBytes) + "\"}");

        Assert.Null(Parse(huge));
    }

    [Fact]
    public void A_lock_of_a_version_this_build_does_not_know_reads_as_unknown()
    {
        Assert.Null(Parse(Encoding.UTF8.GetBytes(Body(v: 2))));
    }

    [Fact]
    public void A_token_that_is_not_the_shape_we_write_is_not_a_token()
    {
        // An empty one would compare equal to another empty one, so one machine could release
        // another's lock. Anything else we cannot compare, and a lock we cannot compare is one to
        // leave alone — including a format a later version might use.
        Assert.Null(Parse(Encoding.UTF8.GetBytes(Body(token: ""))));
        Assert.Null(Parse(Encoding.UTF8.GetBytes(Body(token: "aa"))));
        Assert.Null(Parse(Encoding.UTF8.GetBytes(Body(token: GoodToken.ToUpperInvariant()))));
        Assert.Null(Parse(Encoding.UTF8.GetBytes(Body(token: GoodToken + "0"))));
        Assert.NotNull(Parse(Encoding.UTF8.GetBytes(Body())));
    }

    [Fact]
    public void Duplicate_and_unknown_members_are_refused()
    {
        // Tokens are valid here on purpose, so these fail for the reason the test is named after.
        Assert.Null(Parse(Encoding.UTF8.GetBytes(
            "{\"v\":1,\"pluginId\":\"a\",\"machine\":\"m\",\"user\":\"u\",\"takenAtUtc\":\"x\"," +
            $"\"token\":\"{GoodToken}\",\"token\":\"{GoodToken}\"}}")));

        Assert.Null(Parse(Encoding.UTF8.GetBytes(
            "{\"v\":1,\"pluginId\":\"a\",\"machine\":\"m\",\"user\":\"u\",\"takenAtUtc\":\"x\"," +
            $"\"token\":\"{GoodToken}\",\"somethingElse\":1}}")));
    }

    [Fact]
    public void A_lock_naming_a_different_plugin_is_not_this_lock()
    {
        // A file sitting where this plugin's lock belongs, describing something else. Reporting its
        // contents would name the holder of a different thing.
        Assert.Null(Parse(Encoding.UTF8.GetBytes(Body()), pluginId: "somethingelse"));
    }

    [Fact]
    public void Explicit_nulls_do_not_satisfy_a_required_member()
    {
        // `required` checks only that the member was PRESENT. An explicit null arrives as null and
        // would reach the message the author hears.
        Assert.Null(Parse(Encoding.UTF8.GetBytes(
            $"{{\"v\":1,\"pluginId\":\"a\",\"machine\":null,\"user\":\"u\"," +
            $"\"takenAtUtc\":\"x\",\"token\":\"{GoodToken}\"}}")));
    }

    [Fact]
    public void A_fabricated_lock_cannot_fill_the_error_with_unspeakable_text()
    {
        // This message is read aloud. A 60 KiB user name would announce tens of thousands of
        // characters with no action in them, and embedded newlines would let a hostile server forge
        // extra lines of the message around it.
        Assert.Null(Parse(Encoding.UTF8.GetBytes(Body(user: new string('u', 5_000)))));
        Assert.Null(Parse(Encoding.UTF8.GetBytes(Body(user: "ola\\nsomething the server made up"))));
        Assert.Null(Parse(Encoding.UTF8.GetBytes(Body(machine: new string('m', PublishLock.MaxFieldLength + 1)))));
        Assert.NotNull(Parse(Encoding.UTF8.GetBytes(Body(machine: new string('m', PublishLock.MaxFieldLength)))));
    }

    // ---- whose lock is it ----

    [Fact]
    public void Only_our_own_token_counts_as_ours()
    {
        var ours = PublishLock.NewBody("amethyst");

        Assert.True(PublishLock.IsOurs(PublishLock.TryParse(PublishLock.Serialize(ours), "amethyst"), ours));
        Assert.False(PublishLock.IsOurs(null, ours));
        Assert.False(PublishLock.IsOurs(PublishLock.NewBody("amethyst"), ours));
        Assert.False(PublishLock.IsOurs(ours with { Token = ours.Token.ToUpperInvariant() }, ours));
    }

    [Fact]
    public void A_matching_token_under_a_different_plugin_is_not_ours()
    {
        var ours = PublishLock.NewBody("amethyst");
        Assert.False(PublishLock.IsOurs(ours with { PluginId = "someoneelse" }, ours));
    }

    [Fact]
    public void A_holder_describes_itself_even_when_its_timestamp_is_nonsense()
    {
        var body = PublishLock.NewBody("amethyst") with { TakenAtUtc = "whenever", User = "ola", Machine = "desk" };
        Assert.Equal("ola on desk, since whenever", body.Describe());
    }

    private static string Body(int v = 1, string token = GoodToken, string user = "u", string machine = "m") =>
        $"{{\"v\":{v},\"pluginId\":\"a\",\"machine\":\"{machine}\",\"user\":\"{user}\"," +
        $"\"takenAtUtc\":\"2026-07-28T00:00:00Z\",\"token\":\"{token}\"}}";
}
