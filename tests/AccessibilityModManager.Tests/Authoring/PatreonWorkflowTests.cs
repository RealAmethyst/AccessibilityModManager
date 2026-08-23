using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Patreon;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class PatreonWorkflowTests
{
    [Fact]
    public async Task Status_reports_signed_out_without_exposing_any_token()
    {
        var session = new FakeSession();
        var result = await new PatreonWorkflow(session).GetStatusAsync(CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.None, result.ErrorKind);
        Assert.False(result.Value!.IsSignedIn);
        Assert.DoesNotContain("token", string.Join(" ", result.Messages), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sign_in_cancellation_propagates_as_cancellation()
    {
        var session = new FakeSession { CancelSignIn = true };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PatreonWorkflow(session).SignInAsync(cancellation.Token));
    }

    [Fact]
    public async Task Tiers_require_a_signed_in_creator_campaign()
    {
        var signedOut = await new PatreonWorkflow(new FakeSession())
            .GetTiersAsync(CancellationToken.None);
        Assert.Equal(WorkflowErrorKind.Authentication, signedOut.ErrorKind);

        var noCampaign = new FakeSession { Account = Account() };
        var missing = await new PatreonWorkflow(noCampaign).GetTiersAsync(CancellationToken.None);
        Assert.Equal("patreonCampaignMissing", missing.Status);
    }

    [Fact]
    public async Task Tiers_and_post_attachments_are_complete_and_stably_identified()
    {
        var session = new FakeSession
        {
            Account = Account(),
            Campaign = new PatreonOwnCampaign(
                "campaign-1",
                "Blind Soldier",
                new[] { new PatreonTier("tier-1", "Supporter", 500) }),
            Attachments =
            [
                new PatreonPostAttachment(
                    "12345",
                    new Uri("https://cdn.example.invalid/mod.zip"),
                    "mod.zip",
                    123,
                    new[] { "tier-1" })
            ]
        };
        var workflow = new PatreonWorkflow(session);

        var tiers = await workflow.GetTiersAsync(CancellationToken.None);
        var first = await workflow.InspectPostAsync(
            "https://www.patreon.com/posts/blind-soldier-12345",
            CancellationToken.None);
        var second = await workflow.InspectPostAsync(
            "https://www.patreon.com/posts/blind-soldier-12345",
            CancellationToken.None);

        Assert.Equal("tier-1", Assert.Single(tiers.Value!).TierId);
        var attachment = Assert.Single(first.Value!.Attachments);
        Assert.Equal("mod.zip", attachment.FileName);
        Assert.Equal(new[] { "tier-1" }, attachment.RequiredTierIds);
        Assert.Equal(attachment.SelectionId, Assert.Single(second.Value!.Attachments).SelectionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/posts/test-123")]
    [InlineData("https://patreon.com/posts/not-numeric")]
    public async Task Invalid_post_urls_are_rejected_before_session_or_network_use(string url)
    {
        var session = new FakeSession { Account = Account() };
        var result = await new PatreonWorkflow(session).InspectPostAsync(url, CancellationToken.None);

        Assert.Equal(WorkflowErrorKind.Validation, result.ErrorKind);
        Assert.Equal(0, session.ValidateCalls);
    }

    private static PatreonAccount Account() => new()
    {
        AccessToken = "private-access-token",
        RefreshToken = "private-refresh-token",
        ExpiresAt = DateTime.UtcNow.AddHours(1),
        UserId = "user-1",
        FullName = "Author"
    };

    private sealed class FakeSession : IPatreonAuthorSession
    {
        public PatreonAccount? Account { get; set; }
        public PatreonOwnCampaign? Campaign { get; set; }
        public IReadOnlyList<PatreonPostAttachment> Attachments { get; set; } = [];
        public bool CancelSignIn { get; set; }
        public int ValidateCalls { get; private set; }

        public bool IsSignedIn => Account is not null;
        public PatreonAccount? CurrentAccount => Account;
        public PatreonOwnCampaign? OwnCampaign => Campaign;
        public Task LoadAsync() => Task.CompletedTask;

        public Task SignInAsync(CancellationToken ct)
        {
            if (CancelSignIn)
                throw new OperationCanceledException(ct);
            Account = PatreonWorkflowTests.Account();
            return Task.CompletedTask;
        }

        public Task SignOutAsync(CancellationToken ct)
        {
            Account = null;
            Campaign = null;
            return Task.CompletedTask;
        }

        public Task<PatreonOwnCampaign?> RefreshOwnCampaignAsync(CancellationToken ct) =>
            Task.FromResult(Campaign);

        public Task<(IReadOnlyList<PatreonPostAttachment> Attachments, string? DebugFilePath)>
            ValidatePostUrlAsync(string postUrl, CancellationToken ct)
        {
            ValidateCalls++;
            return Task.FromResult((Attachments, (string?)null));
        }
    }
}
