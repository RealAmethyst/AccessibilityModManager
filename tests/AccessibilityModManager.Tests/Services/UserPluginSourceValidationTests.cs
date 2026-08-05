using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Tests.Services;

/// <summary>
/// What the manager will accept out of the settings file.
///
/// <para>These checks run on LOAD, not only when a source is added through the app. The config file
/// is an ordinary file: anything able to write it can append a source, and that route never passes
/// the screen that explains the risk. A rule enforced only where the UI happens to call it is not a
/// rule.</para>
/// </summary>
public sealed class UserPluginSourceValidationTests
{
    private static UserPluginSource Valid(string id = "buu420") =>
        AccessibilityModManager.Tests.Helpers.TestUserSource.Accepted(id, "Buu");

    [Fact]
    public void A_normal_source_is_accepted()
    {
        var result = UserPluginSourceValidation.Accept([Valid()]);

        Assert.Single(result.Accepted);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void A_source_nobody_accepted_the_notice_for_is_not_loaded()
    {
        // The whole reason acceptance is recorded on the DATA: a source written straight into
        // config.json never went through the screen that shows what a source can do.
        var smuggled = Valid();
        smuggled.NoticeAcceptedUtc = null;

        var result = UserPluginSourceValidation.Accept([smuggled]);

        Assert.Empty(result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Contains("confirmed", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_approval_does_not_survive_the_id_being_edited()
    {
        // Acceptance is bound to the identity it was given for, so an approval cannot be inherited
        // by a different developer through an in-place edit of the settings file.
        var edited = Valid();
        edited.PluginId = "someone-else";

        var rejected = Assert.Single(UserPluginSourceValidation.Accept([edited]).Rejected);
        Assert.Contains("confirming again", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_approval_does_not_survive_the_address_being_edited()
    {
        // The likelier version: same developer id, catalog re-pointed somewhere else.
        var edited = Valid();
        edited.IndexUrl = "https://somewhere-else.invalid/index.json";

        var rejected = Assert.Single(UserPluginSourceValidation.Accept([edited]).Rejected);
        Assert.Contains("confirming again", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://example.invalid/index.json")]
    [InlineData("file:///C:/index.json")]
    [InlineData("not a url")]
    [InlineData("")]
    public void An_address_that_is_not_https_is_refused(string url)
    {
        var source = Valid();
        source.IndexUrl = url;

        var result = UserPluginSourceValidation.Accept([source]);

        Assert.Empty(result.Accepted);
        Assert.Single(result.Rejected);
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("has space")]
    [InlineData("")]
    public void An_id_that_could_escape_a_folder_is_refused(string id)
    {
        // The id becomes a receipt and cache folder name, so containment is checked before it is
        // ever combined into a path.
        var source = Valid();
        source.PluginId = id;

        var result = UserPluginSourceValidation.Accept([source]);

        Assert.Empty(result.Accepted);
        Assert.Single(result.Rejected);
    }

    [Fact]
    public void Two_entries_for_one_id_keep_the_first()
    {
        var first = Valid();
        first.DisplayName = "First";
        var second = Valid();
        second.DisplayName = "Second";

        var result = UserPluginSourceValidation.Accept([first, second]);

        Assert.Single(result.Accepted);
        Assert.Equal("First", result.Accepted[0].DisplayName);
        Assert.Single(result.Rejected);
    }

    [Fact]
    public void Ids_differing_only_by_case_are_the_same_entry()
    {
        var result = UserPluginSourceValidation.Accept([Valid("buu420"), Valid("BUU420")]);

        Assert.Single(result.Accepted);
        Assert.Single(result.Rejected);
    }

    [Fact]
    public void The_list_is_bounded_so_a_scribbled_config_cannot_fan_out()
    {
        var many = Enumerable.Range(0, UserPluginSourceValidation.MaxSources + 5)
            .Select(i => Valid($"src{i}"))
            .ToList();

        var result = UserPluginSourceValidation.Accept(many);

        Assert.Equal(UserPluginSourceValidation.MaxSources, result.Accepted.Count);
        Assert.Equal(5, result.Rejected.Count);
    }

    [Fact]
    public void A_missing_list_is_simply_no_sources()
    {
        var result = UserPluginSourceValidation.Accept(null);

        Assert.Empty(result.Accepted);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Every_rejection_says_which_source_and_why()
    {
        // These reach the user as spoken text. A refusal with no subject leaves them wondering
        // which developer went missing.
        var bad = Valid();
        bad.IndexUrl = "http://example.invalid/index.json";

        var rejected = Assert.Single(UserPluginSourceValidation.Accept([bad]).Rejected);

        Assert.False(string.IsNullOrWhiteSpace(rejected.Describe));
        Assert.False(string.IsNullOrWhiteSpace(rejected.Reason));
    }
}
